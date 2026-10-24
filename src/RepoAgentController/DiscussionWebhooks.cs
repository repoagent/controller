using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace RepoAgentController;

sealed record WebhookConfiguration(Uri PublicUrl, int Port, TimeSpan ReconcileInterval)
{
    public Uri Endpoint => new(PublicUrl, "/github/webhook");
}

/// <summary>Authenticates HTTP deliveries and serializes observations with reconciliation.</summary>
sealed class DiscussionWebhooks(GitHub github, Bridge bridge, string statePath, WebhookConfiguration configuration, CheckoutRefresh? refresh = null, DurableWebhookWork? durable = null)
{
    readonly Channel<int> work = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    readonly HashSet<int> queued = [];
    const int MaximumBody = 2 * 1024 * 1024;

    public static Task<bool> DeactivateSaved(GitHub github, string statePath) => new Registration(github, statePath + ".webhook").Deactivate();

    public async Task Run(CancellationToken ct)
    {
        var registration = new Registration(github, statePath + ".webhook");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://+:{configuration.Port}/");
        listener.Start();
        var receive = Receive(listener, registration.Secret, stop.Token);
        Task worker = Task.CompletedTask, reconcile = Task.CompletedTask;
        try
        {
            await registration.Configure(configuration.Endpoint, refresh is not null, ct);
            if (receive.IsCompleted) await receive;
            Log.Write("mode", $"webhook-fixed public-url={configuration.Endpoint} port={configuration.Port} reconcile-seconds={configuration.ReconcileInterval.TotalSeconds:F0}");
            Log.Write("ready", $"repository={github.Repository} actor={github.AgentLogin} mode=webhook-fixed");
            if (durable is not null) Log.Write("durable-work-enabled", "periodic-reconciliation=false queued-hints=true");
            if (durable is null) Schedule(0); // Durable mode holds its startup hint before any Git/native work.
            worker = durable is null ? Observe(stop.Token) : ObserveDurable(stop.Token);
            reconcile = durable is null ? Reconcile(stop.Token) : CompleteDurable(stop.Token);
            var completed = await Task.WhenAny(receive, worker, reconcile);
            await completed;
        }
        finally
        {
            await stop.CancelAsync();
            // The runtime token is cancelled: remote cleanup gets its own bounded budget.
            // A normal idle replica shutdown must leave the signed wake route registered.
            if (durable is null) await registration.Deactivate();
            listener.Close();
            work.Writer.TryComplete();
            try { await Task.WhenAll(receive, worker, reconcile); }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }
    }

    bool Schedule(int number)
    {
        lock (queued)
        {
            if (!queued.Add(number)) return true;
            if (work.Writer.TryWrite(number)) return true;
            queued.Remove(number);
            return false;
        }
    }

    async Task Observe(CancellationToken ct)
    {
        await foreach (var number in work.Reader.ReadAllAsync(ct))
        {
            // Remove before reading: a delivery during this observation schedules another read.
            lock (queued) queued.Remove(number);
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            var succeeded = number == 0 ? await bridge.Poll(ct) : await bridge.Observe(number, ct);
            Log.Write(number == 0 ? "reconciliation-completed" : "webhook-observed",
                $"discussion={(number == 0 ? "all" : number.ToString())} success={succeeded} elapsed-ms={System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}");
        }
    }

    async Task Reconcile(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(configuration.ReconcileInterval);
        while (await timer.WaitForNextTickAsync(ct)) Schedule(0);
    }

    async Task ObserveDurable(CancellationToken ct)
    {
        while (true)
        {
            var lease = await durable!.Receive(ct);
            if (lease.Hint is DurableWorkHint.CheckoutRefresh) refresh!.Request();
            else
            {
                var succeeded = lease.Hint is DurableWorkHint.Discussion discussion ? await bridge.Observe(discussion.Number, ct) : await bridge.Poll(ct);
                if (!succeeded)
                {
                    // One failed observation keeps its work but must not stop the shared worker or later hints.
                    await durable.Retry(lease, ct);
                    continue;
                }
                Log.Write("durable-webhook-observed", $"hint={lease.Hint}");
            }
            durable.Observed(lease);
        }
    }

    async Task CompleteDurable(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct)) await durable!.CompleteAtIdle(refresh!.CommitAtIdle, ct);
    }

    async Task Receive(HttpListener listener, string secret, CancellationToken ct)
    {
        using var slots = new SemaphoreSlim(16);
        var requests = new List<Task>();
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var context = await listener.GetContextAsync().WaitAsync(ct);
                if (!slots.Wait(0)) { Respond(context, 503); continue; }
                requests.RemoveAll(task => task.IsCompleted);
                requests.Add(Handle(context));
            }
        }
        finally { await Task.WhenAll(requests); }

        async Task Handle(HttpListenerContext context)
        {
            var status = 500;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                status = await Accept(context.Request, secret, deadline.Token);
            }
            catch (OperationCanceledException) { status = 408; }
            catch (JsonException) { status = 400; }
            catch (Exception error) { Log.Write("webhook-error", error.Message); }
            finally { Respond(context, status); slots.Release(); }
        }
    }

    async Task<int> Accept(HttpListenerRequest request, string secret, CancellationToken ct)
    {
        if (request.Url?.AbsolutePath.TrimEnd('/') != "/github/webhook") return 404;
        if (request.HttpMethod != "POST") return 405;
        if (request.ContentLength64 > MaximumBody) return 413;
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int length;
        while ((length = await request.InputStream.ReadAsync(buffer, ct)) != 0)
        {
            if (body.Length + length > MaximumBody) return 413;
            body.Write(buffer, 0, length);
        }
        var signature = request.Headers["X-Hub-Signature-256"] ?? "";
        byte[] supplied;
        try { supplied = signature.StartsWith("sha256=", StringComparison.Ordinal) ? Convert.FromHexString(signature[7..]) : []; }
        catch (FormatException) { supplied = []; }
        var bytes = body.ToArray();
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), bytes), supplied))
        {
            Log.Write("webhook-rejected", "reason=signature");
            return 401;
        }
        if (JsonNode.Parse(bytes) is not JsonObject payload) return 400;
        if (!string.Equals(payload["repository"]?["full_name"]?.ToString(), github.Repository, StringComparison.OrdinalIgnoreCase))
        {
            Log.Write("webhook-rejected", "reason=repository");
            return 403;
        }
        if (!Guid.TryParse(request.Headers["X-GitHub-Delivery"], out var delivery)) return 400;
        if (durable is not null && (payload["repository"]?["id"] is not JsonValue immutableId ||
            !immutableId.TryGetValue<long>(out var expectedId) || expectedId != refresh!.RepositoryId)) return 403;
        var eventName = request.Headers["X-GitHub-Event"];
        if (eventName == "ping") { Log.Write("webhook-ping", $"delivery={delivery}"); return 204; }
        if (eventName == "push")
        {
            if (refresh is null) return 204;
            if (payload["repository"]?["id"] is not JsonValue identity || !identity.TryGetValue<long>(out var repositoryId) ||
                repositoryId != refresh.RepositoryId) return 403;
            if (payload["ref"] is not JsonValue reference || !reference.TryGetValue<string>(out var pushedRef) ||
                pushedRef.Length is 0 or > 1024 || pushedRef.Any(char.IsControl)) return 400;
            if (pushedRef != refresh.DefaultRef) return 204;
            if (durable is not null) await durable.Enqueue(new DurableWorkHint.CheckoutRefresh(), ct);
            else refresh.Request(); // Persist the hint before acknowledging; only the worker touches Git/native.
            Log.Write("webhook-delivery", $"delivery={delivery} event=push scheduled=True");
            return 202;
        }
        if (eventName is not ("discussion" or "discussion_comment")) return 204;
        var action = payload["action"]?.ToString() ?? "";
        if (action.Length is 0 or > 64 || action.Any(c => !char.IsAsciiLetterLower(c) && c != '_')) return 400;
        if (payload["discussion"]?["number"] is not JsonValue value || !value.TryGetValue<int>(out var number) || number < 1) return 400;
        var sender = Json.Object(("login", payload["sender"]?["login"]?.ToString()), ("__typename", payload["sender"]?["type"]?.ToString()));
        if (eventName == "discussion_comment" && github.IsAgentActor(sender))
        {
            Log.Write("webhook-ignored", $"delivery={delivery} discussion={number} reason=own-comment");
            return 204;
        }
        if (durable is not null) await durable.Enqueue(new DurableWorkHint.Discussion(number), ct);
        else if (!Schedule(number)) return 503;
        Log.Write("webhook-delivery", $"delivery={delivery} event={eventName} action={action} discussion={number} scheduled=True");
        return 202;
    }

    static void Respond(HttpListenerContext context, int status)
    {
        try { context.Response.StatusCode = status; context.Response.ContentLength64 = 0; context.Response.Close(); }
        catch (Exception error) when (error is IOException or HttpListenerException or ObjectDisposedException) { }
    }

    sealed class Registration
    {
        readonly GitHub github;
        readonly string path;
        readonly JsonObject settings;
        public string Secret => settings.Text("secret");

        public Registration(GitHub github, string directory)
        {
            this.github = github;
            GitHubAppCredentials.SecureDirectory(directory);
            path = Path.Combine(directory, "hook.json");
            settings = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() :
                Json.Object(("repository", github.Repository), ("secret", Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))));
            if (!string.Equals(settings.Text("repository"), github.Repository, StringComparison.OrdinalIgnoreCase) || Secret.Length != 64)
                throw new InvalidOperationException("Stored repository webhook configuration does not match this repository");
            Log.Protect(Secret);
            Save();
        }

        public async Task Configure(Uri endpoint, bool clone, CancellationToken ct)
        {
            var hook = settings["hookId"]?.GetValue<long>();
            var input = Json.Object(("name", "web"), ("active", true),
                ("events", clone ? new JsonArray("discussion", "discussion_comment", "push") : new JsonArray("discussion", "discussion_comment")),
                ("config", Json.Object(("url", endpoint.AbsoluteUri), ("content_type", "json"), ("insecure_ssl", "0"), ("secret", Secret))));
            JsonNode? result;
            try { result = await github.Rest(hook is null ? "POST" : "PATCH", hook is null ? "hooks" : $"hooks/{hook}", input, ct); }
            catch (IOException error) { throw new IOException("Repository webhook registration failed; check the saved hook and approved Webhooks read/write permission. " + error.Message, error); }
            var id = result?["id"]?.GetValue<long>() ?? 0;
            if (id <= 0 || (hook is not null && id != hook)) throw new IOException("GitHub returned an unexpected repository webhook ID");
            settings["hookId"] = id;
            settings["url"] = endpoint.AbsoluteUri;
            Save();
            Log.Write(hook is null ? "webhook-registered" : "webhook-updated", $"hook={id} url={endpoint}");
        }

        public async Task<bool> Deactivate()
        {
            if (settings["hookId"]?.GetValue<long>() is not { } hook) return true;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                // Omit config entirely: preserve the secret, URL and subscribed events.
                var result = await github.Rest("PATCH", $"hooks/{hook}", Json.Object(("active", false)), cleanup.Token);
                if (result?["id"]?.GetValue<long>() != hook || result?["active"]?.GetValue<bool>() != false)
                    throw new IOException("GitHub did not confirm that the saved hook is inactive");
                Log.Write("webhook-disabled", $"hook={hook}");
                return true;
            }
            catch (Exception error)
            {
                Log.Write("webhook-disable-error", $"hook={hook}; disable not confirmed; manually deactivate the saved hook in repository Settings > Webhooks. {error.Message}");
                return false;
            }
        }

        void Save()
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, settings.ToJsonString());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
        }
    }
}
