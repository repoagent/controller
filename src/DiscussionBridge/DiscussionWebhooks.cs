using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace DiscussionBridge;

sealed record WebhookConfiguration(Uri PublicUrl, int Port, TimeSpan ReconcileInterval)
{
    public Uri Endpoint => new(PublicUrl, "/github/webhook");
}

/// <summary>Authenticates HTTP deliveries and serializes observations with reconciliation.</summary>
sealed class DiscussionWebhooks(GitHub github, Bridge bridge, string statePath, WebhookConfiguration configuration)
{
    readonly Channel<int> work = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    readonly HashSet<int> queued = [];
    const int MaximumBody = 2 * 1024 * 1024;

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
            await registration.Configure(configuration.Endpoint, ct);
            if (receive.IsCompleted) await receive;
            Log.Write("mode", $"webhook-fixed public-url={configuration.Endpoint} port={configuration.Port} reconcile-seconds={configuration.ReconcileInterval.TotalSeconds:F0}");
            Log.Write("ready", $"repository={github.Repository} actor={github.AgentLogin} mode=webhook-fixed");
            Schedule(0); // Reconcile existing content once before relying on notifications.
            worker = Observe(stop.Token);
            reconcile = Reconcile(stop.Token);
            var completed = await Task.WhenAny(receive, worker, reconcile);
            await completed;
        }
        finally
        {
            await stop.CancelAsync();
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
        var eventName = request.Headers["X-GitHub-Event"];
        if (eventName == "ping") { Log.Write("webhook-ping", $"delivery={delivery}"); return 204; }
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
        if (!Schedule(number)) return 503;
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

        public async Task Configure(Uri endpoint, CancellationToken ct)
        {
            var hook = settings["hookId"]?.GetValue<long>();
            var input = Json.Object(("name", "web"), ("active", true),
                ("events", new JsonArray("discussion", "discussion_comment")),
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

        void Save()
        {
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, settings.ToJsonString());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
        }
    }
}
