using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
sealed class DiscussionWebhooks : IDisposable
{
    readonly IWebhookRegistration registration;
    readonly TimeProvider time;
    readonly ControllerWebhooks observations;
    readonly SemaphoreSlim slots = new(16);
    const int MaximumBody = 2 * 1024 * 1024;

    public DiscussionWebhooks(GitHub github, Bridge bridge, IWebhookRegistration registration, WebhookConfiguration configuration,
        TimeProvider time, CheckoutRefresh? refresh = null)
    {
        (this.registration, this.time) = (registration, time);
        observations = new(github.Repository, github.Identity, bridge, registration, configuration.Endpoint, configuration.Port,
            configuration.ReconcileInterval, time, new ControllerLog(), refresh);
    }

    public void Dispose() { slots.Dispose(); observations.Dispose(); }
    public Task Run(CancellationToken ct) => observations.Run(ct, ct);
    public Task Run(CancellationToken stoppingToken, CancellationToken activityToken) => observations.Run(stoppingToken, activityToken);
    /// <summary>Answers one delivery to POST /github/webhook with an empty response and its status code.</summary>
    public async Task Receive(HttpContext context)
    {
        if (!slots.Wait(0)) { context.Response.StatusCode = 503; return; }
        var status = 500;
        try
        {
            // The body must arrive within five seconds of the controller's time.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            using var reading = CancellationTokenSource.CreateLinkedTokenSource(observations.Stopping, context.RequestAborted, deadline.Token);
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
        var repository = payload["repository"] as JsonObject;
        var discussion = payload["discussion"] as JsonObject;
        var eventName = request.Headers["X-GitHub-Event"].ToString();
        var observation = new WebhookObservation(eventName switch
        {
            "ping" => WebhookChange.Ping,
            "push" => WebhookChange.Push,
            "discussion" => WebhookChange.Discussion,
            "discussion_comment" => WebhookChange.Comment,
            _ => WebhookChange.Other
        }, Guid.TryParse(request.Headers["X-GitHub-Delivery"].ToString(), out var delivery) ? delivery : null,
            repository?["full_name"]?.ToString() ?? "")
        {
            Action = payload["action"]?.ToString() ?? "",
            DiscussionNumber = discussion?["number"] is JsonValue number && number.TryGetValue<int>(out var parsedNumber) ? parsedNumber : null,
            DiscussionAuthor = Account(discussion?["user"]),
            DiscussionBody = discussion?["body"]?.ToString() ?? "",
            Sender = Account(payload["sender"]),
            RepositoryId = repository?["id"] is JsonValue id && id.TryGetValue<long>(out var parsedId) ? parsedId : null,
            PushedRef = payload["ref"].OptionalText()
        };
        return observations.Accept(observation) switch
        {
            WebhookDisposition.Accepted => 202,
            WebhookDisposition.Ignored => 204,
            WebhookDisposition.Invalid => 400,
            WebhookDisposition.WrongRepository => 403,
            _ => 503
        };
    }
    // A webhook payload's user or sender, which GitHub reports as a login and a type such as User or Bot.
    static Actor Account(JsonNode? user) => new(user?["login"]?.ToString() ?? "", user?["type"]?.ToString() ?? "");
}
