using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace RepoAgentController;

/// <summary>Private operator inspection policy over mapped sessions and the existing runtime's read-only activity port.</summary>
public sealed class SessionActivityInspector(string repository, string lifetime, IActivitySessions sessions,
    IAgentActivity activity, TimeProvider clock, Func<string, string> redact, Func<int>? protectedLength = null)
{
    int reading, watching;
    const int MaximumCursor = 4096;

    public async IAsyncEnumerable<ActivityFrame> Follow(string discussion, int limit = 100, string? cursor = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var starting = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var selection = await Select(discussion, starting.Token).WaitAsync(starting.Token);
        if (Interlocked.Increment(ref watching) > 4)
        {
            Interlocked.Decrement(ref watching);
            throw Unavailable("Four activity viewers are already following sessions.");
        }
        var epoch = activity.ActivityEpoch;
        var watcher = new Watcher(selection.SessionId, epoch, Identity);
        activity.ActivityChanged += watcher.Observe;
        try
        {
            // Subscription precedes the history read. The bounded queue preserves notifications racing the snapshot.
            var snapshot = await Read(discussion, limit, cursor, starting.Token);
            var raced = watcher.Drain(out var snapshotSequence);
            if (PublicationGap(watcher, epoch) is { } initialGap) { yield return new("gap", lifetime, Notice: initialGap); yield break; }
            var items = snapshot.Items.ToDictionary(item => (item.TurnId, item.Id));
            var additional = new List<ActivityItem>();
            foreach (var update in raced)
            {
                var item = Sanitize(update, Math.Min(32768, 128000 / Math.Max(1, snapshot.Items.Count)));
                if (items.TryGetValue((item.TurnId, item.Id), out var previous))
                {
                    if (!Reconcile(previous, item, out var reconciledItem))
                    { yield return new("gap", lifetime, Notice: "Conflicting history and live item baselines; read a new snapshot."); yield break; }
                    items[(item.TurnId, item.Id)] = reconciledItem;
                }
                else if (items.Count < limit) items[(item.TurnId, item.Id)] = item;
                else additional.Add(item);
            }
            var reconciled = items.Values.Select(item => Sanitize(item, Math.Min(32768, 128000 / Math.Max(1, items.Count)), false)).ToArray();
            if (PublicationGap(watcher, epoch) is { } snapshotGap) { yield return new("gap", lifetime, Notice: snapshotGap); yield break; }
            yield return new("snapshot", lifetime, snapshot with
            { Items = reconciled, Incomplete = snapshot.Incomplete || reconciled.Any(item => item.Incomplete || item.Truncated), Sequence = snapshotSequence });
            // Items outside the requested history page are upserts, so the provider continuation still describes that page.
            foreach (var item in additional.DistinctBy(item => (item.TurnId, item.Id)))
            {
                if (!await StillMapped(discussion, selection, ct))
                { yield return new("gap", lifetime, Notice: "Discussion mapping changed or became unavailable; select it again."); yield break; }
                if (PublicationGap(watcher, epoch) is { } interrupted)
                { yield return new("gap", lifetime, Notice: interrupted, Sequence: watcher.Sequence); yield break; }
                yield return new("upsert", lifetime, Item: item, Sequence: snapshotSequence);
            }
            Task<bool>? waiting = null;
            while (!ct.IsCancellationRequested)
            {
                if (PublicationGap(watcher, epoch) is { } gap) { yield return new("gap", lifetime, Notice: gap, Sequence: watcher.Sequence); yield break; }
                waiting ??= watcher.Wait(ct);
                bool available;
                var heartbeat = false;
                try { available = await waiting.WaitAsync(TimeSpan.FromSeconds(15), clock, ct); }
                catch (TimeoutException) { available = false; heartbeat = true; }
                if (heartbeat)
                {
                    if (!await StillMapped(discussion, selection, ct))
                    { yield return new("gap", lifetime, Notice: "Discussion mapping changed or became unavailable; select it again."); yield break; }
                    if (PublicationGap(watcher, epoch) is { } heartbeatGap)
                    { yield return new("gap", lifetime, Notice: heartbeatGap); yield break; }
                    yield return new("heartbeat", lifetime, Sequence: watcher.Sequence);
                    continue;
                }
                if (!available) continue;
                waiting = null;
                var updates = watcher.Drain(out var updateSequence);
                foreach (var item in updates)
                {
                    if (!await StillMapped(discussion, selection, ct))
                    { yield return new("gap", lifetime, Notice: "Discussion mapping changed or became unavailable; select it again."); yield break; }
                    if (PublicationGap(watcher, epoch) is { } interrupted)
                    { yield return new("gap", lifetime, Notice: interrupted, Sequence: watcher.Sequence); yield break; }
                    yield return new("upsert", lifetime, Item: Sanitize(item, 32768), Sequence: updateSequence);
                }
            }
        }
        finally
        {
            activity.ActivityChanged -= watcher.Observe;
            Interlocked.Decrement(ref watching);
        }
    }

    string? PublicationGap(Watcher watcher, int epoch) => activity.ActivityEpoch == epoch ? watcher.Gap :
        "Runtime changed or stopped; read a new snapshot.";

    sealed class Watcher(string session, int epoch, Func<string, bool> identity)
    {
        readonly object gate = new();
        readonly Channel<ActivityItem> queue = Channel.CreateBounded<ActivityItem>(new BoundedChannelOptions(256)
        { SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false });
        int bytes;
        string? gap;
        long sequence;
        public string? Gap { get { lock (gate) return gap; } }
        public long Sequence { get { lock (gate) return sequence; } }
        public Task<bool> Wait(CancellationToken ct) => queue.Reader.WaitToReadAsync(ct).AsTask();
        public void Observe(AgentActivityEvent change)
        {
            lock (gate)
            {
                if (gap is not null) return;
                if (change.Epoch != epoch || change is ActivityRuntimeChanged)
                { Stop("Runtime changed or stopped; read a new snapshot."); return; }
                if (change is ActivityTextAppended appended && appended.SessionId == session)
                { Stop("An activity delta has no complete item baseline; read a new snapshot."); return; }
                if (change is ActivityStreamGap lost && lost.SessionId == session)
                { Stop("Runtime activity was incomplete; read a new snapshot."); return; }
                if (change is not ActivityItemObserved { Item: var item } || item.SessionId != session) return;
                if (!identity(item.Id) || !identity(item.TurnId))
                { Stop("Activity item identity is invalid; read a new snapshot."); return; }
                var size = Size(item);
                if (size > 1024 * 1024 - bytes || !queue.Writer.TryWrite(item))
                { Stop("Viewer fell behind the bounded activity queue; read a new snapshot."); return; }
                bytes += size;
                sequence++;
            }
        }
        public ActivityItem[] Drain(out long watermark)
        {
            lock (gate)
            {
                var items = new List<ActivityItem>();
                while (queue.Reader.TryRead(out var item)) { bytes -= Size(item); items.Add(item); }
                watermark = sequence;
                return items.GroupBy(item => (item.TurnId, item.Id)).Select(group => group.Last()).ToArray();
            }
        }
        void Stop(string reason) { gap = reason; queue.Writer.TryComplete(); }
        static int Size(ActivityItem item)
        {
            long size = 2048L + (item.Name?.Length ?? 0) * 3L + (item.Arguments?.Length ?? 0) * 3L +
                (item.Result?.Length ?? 0) * 3L + (item.Text?.Length ?? 0) * 3L;
            if (item.Reads is { } reads) foreach (var read in reads) size += read.Path.Length * 3L;
            return (int)Math.Min(int.MaxValue, size);
        }
    }

    static bool Reconcile(ActivityItem history, ActivityItem notification, out ActivityItem item)
    {
        static bool Final(ActivityItem value) => value.State is ActivityItemState.Completed or ActivityItemState.Failed;
        if (Final(history) && !Final(notification)) { item = history; return true; }
        item = notification;
        if (Final(notification)) return true;
        static bool Compatible(string? left, string? right) => left is null || right is null ||
            left.StartsWith(right, StringComparison.Ordinal) || right.StartsWith(left, StringComparison.Ordinal);
        if (!Compatible(history.Text, notification.Text) || !Compatible(history.Result, notification.Result)) return false;
        static string? Longest(string? left, string? right) => (left?.Length ?? 0) > (right?.Length ?? 0) ? left : right;
        item = notification with { Text = Longest(history.Text, notification.Text), Result = Longest(history.Result, notification.Result) };
        return true;
    }

    public async Task<ActivityInventory> List(CancellationToken ct = default)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var mapped = await Mappings(bounded.Token).WaitAsync(bounded.Token);
        return new(lifetime, mapped.Take(100).ToArray(), Math.Max(0, mapped.Count - 100));
    }

    public async Task<ActivitySnapshot> Read(string discussion, int limit = 100, string? cursor = null, CancellationToken ct = default)
    {
        if (limit is < 1 or > 100) throw Unavailable("Activity limit must be between 1 and 100.");
        var caller = ct;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = bounded.Token;
        var selection = await Select(discussion, ct).WaitAsync(ct);
        var epoch = activity.ActivityEpoch;
        var providerCursor = Decode(cursor, selection.SessionId, epoch);
        if (Interlocked.Increment(ref reading) > 2)
        {
            Interlocked.Decrement(ref reading);
            throw Unavailable("Two activity history reads are already in progress.");
        }
        Task<AgentActivityPage>? request = null;
        try
        {
            request = activity.ReadActivity(new(selection.SessionId, limit, providerCursor), bounded.Token);
            var page = await request.WaitAsync(bounded.Token);
            if (epoch != activity.ActivityEpoch || (await Select(discussion, ct).WaitAsync(ct)) != selection)
                throw Unavailable("Activity mapping or runtime changed; select the Discussion again.");
            if (page.Items.Count > limit || page.Items.Any(item => item.SessionId != selection.SessionId ||
                    !Identity(item.Id) || !Identity(item.TurnId)))
                throw Unavailable("Activity history returned an invalid page.");
            if (page.NextCursor is { } next && (next.Length > MaximumCursor || next == providerCursor))
                throw Unavailable("Activity history returned an invalid continuation.");
            // Leave room for JSON escaping and stable item metadata in the 1 MiB wire limit.
            var budget = Math.Min(32768, 128000 / Math.Max(1, page.Items.Count));
            var safe = page.Items.Select(item => Sanitize(item, budget)).ToArray();
            return new(lifetime, epoch, selection, safe,
                Encode(page.NextCursor, selection.SessionId, epoch), page.SuppressedItems, page.Incomplete || safe.Any(item => item.Incomplete || item.Truncated),
                page.Notice is null ? null : "The runtime reports incomplete or unsupported history.");
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            throw Unavailable("Activity history read exceeded five seconds.");
        }
        finally
        {
            // Non-cooperative reads retain their slot until they finish; timeouts cannot create unbounded work.
            if (request is { IsCompleted: false })
                _ = request.ContinueWith(completed => { _ = completed.Exception; Interlocked.Decrement(ref reading); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            else Interlocked.Decrement(ref reading);
        }
    }

    async Task<ActivitySession> Select(string discussion, CancellationToken ct)
    {
        var target = Number(discussion, out var number)
            ? $"https://github.com/{repository}/discussions/{number.ToString(CultureInfo.InvariantCulture)}" : discussion;
        if (!Discussion(target)) throw Unavailable("Select a mapped Discussion number or its exact repository URL.");
        var matched = (await Mappings(ct)).Where(item => item.Discussion == target).ToArray();
        return matched.Length == 1 ? matched[0] : throw Unavailable("The Discussion has no unambiguous controller-mapped session.");
    }

    async Task<ActivitySession> SelectBounded(string discussion, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), clock);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        return await Select(discussion, bounded.Token).WaitAsync(bounded.Token);
    }

    async Task<bool> StillMapped(string discussion, ActivitySession selected, CancellationToken ct)
    {
        try { return await SelectBounded(discussion, ct) == selected; }
        catch (IOException) { return false; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return false; }
    }

    async Task<IReadOnlyList<ActivitySession>> Mappings(CancellationToken ct)
    {
        var mapped = await sessions.ActivitySessions(ct);
        var discussionCounts = mapped.GroupBy(item => item.Discussion, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var sessionCounts = mapped.GroupBy(item => item.SessionId, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        return mapped.Where(item => !item.Dashboard && Discussion(item.Discussion) && redact(item.Discussion) == item.Discussion && Identity(item.SessionId) &&
                discussionCounts[item.Discussion] == 1 && sessionCounts[item.SessionId] == 1)
            .OrderBy(item => item.Discussion, StringComparer.Ordinal).ToArray();
    }

    bool Discussion(string value)
    {
        var prefix = $"https://github.com/{repository}/discussions/";
        return value.StartsWith(prefix, StringComparison.Ordinal) && Number(value[prefix.Length..], out var number) &&
            value[prefix.Length..] == number.ToString(CultureInfo.InvariantCulture);
    }

    static bool Number(string value, out int number) => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;
    static bool SafeIdentity(string value) => value.Length is > 0 and <= 128 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':' or '/');
    bool Identity(string value) => SafeIdentity(value) && redact(value) == value;

    string? Decode(string? cursor, string session, int epoch)
    {
        if (cursor is null) return null;
        if (cursor.Length > MaximumCursor * 2) throw Unavailable("Invalid activity continuation.");
        try
        {
            var fields = Encoding.UTF8.GetString(Convert.FromBase64String(cursor)).Split('\0');
            if (fields.Length == 5 && fields[0] == "1" && fields[1] == lifetime &&
                fields[2] == epoch.ToString(CultureInfo.InvariantCulture) && fields[3] == session && fields[4].Length <= MaximumCursor)
                return fields[4];
        }
        catch (FormatException) { }
        throw Unavailable("Activity continuation is invalid or belongs to a previous mapping or runtime lifetime.");
    }

    string? Encode(string? cursor, string session, int epoch) => cursor is null ? null :
        Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join('\0', "1", lifetime, epoch.ToString(CultureInfo.InvariantCulture), session, cursor)));

    ActivityItem Sanitize(ActivityItem item, int budget, bool retainSuffix = true)
    {
        if (item.Kind is ActivityItemKind.Omitted or ActivityItemKind.Unsupported)
            return item with
            {
                Name = item.Kind == ActivityItemKind.Omitted ? "Private activity omitted" : "Unsupported activity item",
                Arguments = null,
                Result = null,
                Text = null,
                ExitCode = null,
                Reads = null,
                Incomplete = true
            };
        var truncated = item.Truncated;
        var incomplete = item.Incomplete;
        string? Clean(string? text, bool streaming = false)
        {
            if (text is null) return null;
            if (text.Length > 2 * 1024 * 1024) { truncated = true; text = "[oversized text omitted]"; }
            var output = new StringBuilder();
            var safe = redact(text);
            if (streaming && item.Truncated && (protectedLength?.Invoke() ?? 0) > 65536)
                safe = "[truncated private text omitted]";
            if (retainSuffix && streaming && item.State == ActivityItemState.Running)
            {
                // Keep a suffix private until completion so split deltas cannot reveal a protected secret prefix.
                var suffix = protectedLength?.Invoke() ?? 0;
                incomplete |= suffix > 0;
                if (suffix > 65536) { truncated = true; safe = "[running text withheld until completion]"; }
                else safe = safe[..Math.Max(0, safe.Length - suffix)];
            }
            foreach (var rune in safe.EnumerateRunes())
            {
                var value = rune.Value;
                var encoded = (value < 32 && value is not (9 or 10)) || value is >= 127 and <= 159 or >= 0x202A and <= 0x202E or >= 0x2066 and <= 0x2069
                    ? $"\\u{value:X4}" : rune.ToString();
                var size = Encoding.UTF8.GetByteCount(encoded);
                if (size > budget) { truncated = true; break; }
                budget -= size;
                output.Append(encoded);
            }
            return output.ToString();
        }
        var name = Clean(item.Name);
        var arguments = Clean(item.Arguments);
        var result = Clean(item.Result, true);
        var text = Clean(item.Text, true);
        var reads = item.Reads?.Take(20).Select(read => new InstructionReadEvidence(Clean(read.Path) ?? "")).ToArray();
        return item with { Name = name, Arguments = arguments, Result = result, Text = text, Reads = reads, Truncated = truncated, Incomplete = incomplete };
    }

    static ActivityHistoryUnavailable Unavailable(string reason) => new(reason);
}
