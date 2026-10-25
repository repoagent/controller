namespace RepoAgent.Auth.Tests;

// The broker state the core sees, in memory, with the guarantees of the Table adapter the core relies on:
// - every Add and Replace issues a new version, even when nothing changed, so a write fences every writer before it;
// - a transaction is atomic: a conflict (Add of an existing record, or a version that is no longer current) writes nothing;
// - Replace or Delete of a missing record fails as unavailable, as Table's 404 does, and is never a conflict to retry;
// - rows are stored and read through the records' own mapping, and every read returns a fresh copy;
// - nothing exists until a test seeds it, so quota rows exist only when seeded, as offline initialization does;
// - listing is ordinal, at most 32 records a page, and can answer an empty page that still carries a continuation;
//   a listed row is mapped only when the caller reads it, as the Table adapter does.
// Hooks run outside the store's lock: BeforeRead, BeforeList and BeforeCommit may throw or commit competing writes re-entrantly, and
// AfterCommit may throw to lose the response of a transaction that did commit.
internal sealed class InMemoryBrokerState : IBrokerState
{
    private readonly Lock sync = new();
    private readonly Dictionary<(string Scope, string Name), (Dictionary<string, object> Row, StateVersion Version)> rows = [];
    private long versions;
    private int reads, commits, conflicts, lists;

    public Func<StateKey, Task>? BeforeRead { get; set; }
    public Func<StateTransaction, Task>? BeforeCommit { get; set; }
    public Func<StateTransaction, Task>? AfterCommit { get; set; }
    // Called with the listed range's scope.
    public Func<string, Task>? BeforeList { get; set; }
    public int PageSize { get; set; } = 32;
    // The next listing calls answer an empty page that carries a continuation to the same position.
    public int EmptyPages { get; set; }
    public int Reads => reads;
    public int Lists => lists;
    public int Commits => commits;
    // Transactions that failed with a StateConflict and wrote nothing.
    public int Conflicts => conflicts;

    public async Task<Versioned<T>?> ReadAsync<T>(StateKey<T> key, CancellationToken ct) where T : BrokerRecord, new()
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref reads);
        if (BeforeRead is { } before) await before(key);
        Dictionary<string, object> row;
        StateVersion version;
        lock (sync)
        {
            if (!rows.TryGetValue((key.Scope, key.Name), out var stored)) return null;
            (row, version) = (new(stored.Row), stored.Version);
        }
        return new(BrokerRecord.Read<T>(row), version);
    }

    public async Task<IReadOnlyList<StateVersion?>> CommitAsync(StateTransaction transaction, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref commits);
        if (BeforeCommit is { } before) await before(transaction);
        var writes = transaction.Writes;
        if (writes.Count is 0 or > 32 || writes.Select(write => write.Key.Scope).Distinct().Count() != 1
            || writes.Select(write => write.Key.Name).Distinct().Count() != writes.Count)
            throw new InvalidOperationException("A transaction writes 1 to 32 distinct records of one scope.");
        var result = new StateVersion?[writes.Count];
        lock (sync)
        {
            foreach (var write in writes)
            {
                var exists = rows.TryGetValue((write.Key.Scope, write.Key.Name), out var current);
                if (write.Kind == StateWriteKind.Add ? exists : exists && current.Version != write.Version)
                {
                    conflicts++;
                    throw new StateConflict();
                }
                if (write.Kind != StateWriteKind.Add && !exists) throw new BrokerStateUnavailable("The record to change does not exist.");
            }
            for (var i = 0; i < writes.Count; i++)
            {
                var key = (writes[i].Key.Scope, writes[i].Key.Name);
                if (writes[i].Kind == StateWriteKind.Delete) rows.Remove(key);
                else rows[key] = (new(writes[i].Row!), result[i] = NextVersion());
            }
        }
        if (AfterCommit is { } after) await after(transaction);
        return result;
    }

    public async Task<StatePage<T>> ListAsync<T>(StateRange<T> range, string after, string? continuation, CancellationToken ct)
        where T : BrokerRecord, new()
    {
        ct.ThrowIfCancellationRequested();
        Interlocked.Increment(ref lists);
        if (BeforeList is { } before) await before(range.Scope);
        var start = continuation is null ? after : continuation.StartsWith("after:", StringComparison.Ordinal)
            ? continuation["after:".Length..] : throw new BrokerStateUnavailable("Unknown continuation.");
        List<(string Name, Dictionary<string, object> Row, StateVersion Version)> listed;
        lock (sync)
        {
            if (EmptyPages > 0)
            {
                EmptyPages--;
                return new([], "after:" + start);
            }
            listed = [.. rows.Where(row => row.Key.Scope == range.Scope
                    && string.CompareOrdinal(row.Key.Name, range.From) >= 0 && string.CompareOrdinal(row.Key.Name, range.To) < 0
                    && string.CompareOrdinal(row.Key.Name, start) > 0)
                .OrderBy(row => row.Key.Name, StringComparer.Ordinal)
                .Select(row => (row.Key.Name, new Dictionary<string, object>(row.Value.Row), row.Value.Version))];
        }
        var page = listed.Take(Math.Min(PageSize, 32)).ToList();
        return new([.. page.Select(row => new ListedRecord<T>(new(range.Scope, row.Name), row.Row, row.Version))],
            listed.Count > page.Count ? "after:" + page[^1].Name : null);
    }

    // Writes a record the way another writer would, with a new version.
    public StateVersion Seed<T>(StateKey<T> key, T record) where T : BrokerRecord, new() => Put(key.Scope, key.Name, record.ToRow());

    // Writes any row, including one no record can read, with a new version.
    public StateVersion Put(string scope, string name, IReadOnlyDictionary<string, object> row)
    {
        lock (sync)
        {
            var version = NextVersion();
            rows[(scope, name)] = (new(row), version);
            return version;
        }
    }

    // Changes a stored row the way another writer would, with a new version.
    public StateVersion Change(StateKey key, Action<Dictionary<string, object>> change)
    {
        var row = Row(key) ?? throw new InvalidOperationException("No such record: " + key);
        change(row);
        return Put(key.Scope, key.Name, row);
    }

    public T? Get<T>(StateKey<T> key) where T : BrokerRecord, new() => Row(key) is { } row ? BrokerRecord.Read<T>(row) : null;

    public Dictionary<string, object>? Row(StateKey key) => Row(key.Scope, key.Name);

    public Dictionary<string, object>? Row(string scope, string name)
    {
        lock (sync) return rows.TryGetValue((scope, name), out var stored) ? new(stored.Row) : null;
    }

    // Every stored row of a scope, by name, in ordinal name order.
    public IReadOnlyList<(string Name, Dictionary<string, object> Row)> Rows(string scope)
    {
        lock (sync)
            return [.. rows.Where(stored => stored.Key.Scope == scope).OrderBy(stored => stored.Key.Name, StringComparer.Ordinal)
                .Select(stored => (stored.Key.Name, new Dictionary<string, object>(stored.Value.Row)))];
    }

    // Every stored row with its version, in ordinal scope and name order.
    public IReadOnlyList<(string Scope, string Name, Dictionary<string, object> Row, StateVersion Version)> All()
    {
        lock (sync)
            return [.. rows.OrderBy(stored => stored.Key.Scope, StringComparer.Ordinal).ThenBy(stored => stored.Key.Name, StringComparer.Ordinal)
                .Select(stored => (stored.Key.Scope, stored.Key.Name, new Dictionary<string, object>(stored.Value.Row), stored.Value.Version))];
    }

    public StateVersion? Version(StateKey key) => Version(key.Scope, key.Name);

    public StateVersion? Version(string scope, string name)
    {
        lock (sync) return rows.TryGetValue((scope, name), out var stored) ? stored.Version : null;
    }

    public bool Remove(StateKey key)
    {
        lock (sync) return rows.Remove((key.Scope, key.Name));
    }

    private StateVersion NextVersion() => new("v" + ++versions);
}
