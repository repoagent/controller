using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http;

namespace RepoAgentController;

sealed record WebhookConfiguration(Uri PublicUrl, int Port, TimeSpan ReconcileInterval)
{
    public Uri Endpoint => new(PublicUrl, ControllerHttpServer.WebhookPath);
}

/// <summary>
/// Authenticates HTTP deliveries and serializes observations with reconciliation. The controller's HTTP server serves
/// <see cref="Receive"/>; <see cref="Run"/> registers the repository webhook and deactivates it when the controller stops.
/// </summary>
sealed class DiscussionWebhooks(GitHub github, Bridge bridge, IWebhookRegistration registration, WebhookConfiguration configuration, TimeProvider time,
    CheckoutRefresh? refresh = null)
{
    readonly Channel<int> work = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    readonly HashSet<int> queued = [];
    readonly SemaphoreSlim slots = new(16);
    // Cancelled when the controller stops: deliveries still being read then end with 408.
    readonly CancellationTokenSource stop = new();
    const int MaximumBody = 2 * 1024 * 1024;

    /// <summary>Registers the webhook, then observes deliveries and reconciles until <paramref name="ct"/> stops the controller.</summary>
    /// <remarks>The HTTP server must already serve <see cref="Receive"/>: GitHub pings a newly registered hook at once.</remarks>
    public async Task Run(CancellationToken ct)
    {
        using var stopping = ct.UnsafeRegister(_ => stop.Cancel(), null);
        Task worker = Task.CompletedTask, reconcile = Task.CompletedTask;
        try
        {
            await registration.Configure(configuration.Endpoint, refresh is not null, ct);
            Log.Write("mode", $"webhook-fixed public-url={configuration.Endpoint} port={configuration.Port} reconcile-seconds={configuration.ReconcileInterval.TotalSeconds:F0}");
            Log.Write("ready", $"repository={github.Repository} actor={github.AgentLogin} mode=webhook-fixed");
            Schedule(0);
            worker = Observe(stop.Token);
            reconcile = Reconcile(stop.Token);
            var completed = await Task.WhenAny(worker, reconcile);
            await completed;
        }
        finally
        {
            await stop.CancelAsync();
            // The runtime token is cancelled: remote cleanup gets its own bounded budget.
            await registration.Deactivate();
            work.Writer.TryComplete();
            try { await Task.WhenAll(worker, reconcile); }
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

    /// <summary>Answers one delivery to POST /github/webhook with an empty response and its status code.</summary>
    public async Task Receive(HttpContext context)
    {
        if (!slots.Wait(0)) { context.Response.StatusCode = 503; return; }
        var status = 500;
        try
        {
            // The body must arrive within five seconds of the controller's time.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, context.RequestAborted, deadline.Token);
            status = await Accept(context.Request, registration.Secret, reading.Token);
        }
        catch (OperationCanceledException) { status = 408; }
        catch (JsonException) { status = 400; }
        catch (Exception error) { Log.Write("webhook-error", error.Message); }
        finally { slots.Release(); }
        context.Response.StatusCode = status;
    }

    async Task<int> Accept(HttpRequest request, string secret, CancellationToken ct)
    {
        if (request.ContentLength > MaximumBody) return 413;
        using var body = new MemoryStream();
        var buffer = new byte[8192];
        int length;
        while ((length = await request.Body.ReadAsync(buffer, ct)) != 0)
        {
            if (body.Length + length > MaximumBody) return 413;
            body.Write(buffer, 0, length);
        }
        var signature = request.Headers["X-Hub-Signature-256"].ToString();
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
        if (!Guid.TryParse(request.Headers["X-GitHub-Delivery"].ToString(), out var delivery)) return 400;
        var eventName = request.Headers["X-GitHub-Event"].ToString();
        if (eventName == "ping") { Log.Write("webhook-ping", $"delivery={delivery}"); return 204; }
        if (eventName == "push")
        {
            if (refresh is null) return 204;
            if (payload["repository"]?["id"] is not JsonValue identity || !identity.TryGetValue<long>(out var repositoryId) ||
                repositoryId != refresh.RepositoryId) return 403;
            if (payload["ref"].OptionalText() is not { } pushedRef ||
                pushedRef.Length is 0 or > 1024 || pushedRef.Any(char.IsControl)) return 400;
            if (pushedRef != refresh.DefaultRef) return 204;
            refresh.Request(); // Persist the hint before acknowledging; only the worker touches Git/native.
            Log.Write("webhook-delivery", $"delivery={delivery} event=push scheduled=True");
            return 202;
        }
        if (eventName is not ("discussion" or "discussion_comment")) return 204;
        var action = payload["action"]?.ToString() ?? "";
        if (action.Length is 0 or > 64 || action.Any(c => !char.IsAsciiLetterLower(c) && c != '_')) return 400;
        if (payload["discussion"]?["number"] is not JsonValue value || !value.TryGetValue<int>(out var number) || number < 1) return 400;
        var discussion = payload["discussion"]!;
        // Every heartbeat edit of the controller's own status Discussion is notified too; none of it is agent input.
        var app = github.Identity;
        if (app.IsDashboard(Account(discussion["user"]), discussion["body"]?.ToString() ?? "")) return 204;
        if (eventName == "discussion_comment" && app.IsAgentActor(Account(payload["sender"])))
        {
            Log.Write("webhook-ignored", $"delivery={delivery} discussion={number} reason=own-comment");
            return 204;
        }
        if (!Schedule(number)) return 503;
        Log.Write("webhook-delivery", $"delivery={delivery} event={eventName} action={action} discussion={number} scheduled=True");
        return 202;
    }

    // A webhook payload's user or sender, which GitHub reports as a login and a type such as User or Bot.
    static Actor Account(JsonNode? user) => new(user?["login"]?.ToString() ?? "", user?["type"]?.ToString() ?? "");
}
