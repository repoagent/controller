using System.Globalization;
using System.Threading.Channels;

namespace RepoAgentController;

/// <summary>A repository change delivered by an authenticated webhook, translated out of the provider's payload.</summary>
public enum WebhookChange { Other, Ping, Push, Discussion, Comment }

/// <summary>The disposition of one authenticated observation, independent of HTTP status codes.</summary>
public enum WebhookDisposition { Ignored, Accepted, Invalid, WrongRepository, Unavailable }

/// <summary>An authenticated repository notification. Missing or invalid provider fields remain absent until admission validates them.</summary>
public sealed record WebhookObservation(WebhookChange Change, Guid? DeliveryId, string Repository)
{
    public string Action { get; init; } = "";
    public int? DiscussionNumber { get; init; }
    public Actor DiscussionAuthor { get; init; } = new("", "");
    public string DiscussionBody { get; init; } = "";
    public Actor Sender { get; init; } = new("", "");
    public long? RepositoryId { get; init; }
    public string? PushedRef { get; init; }
}

/// <summary>
/// Serializes repository observations and periodic reconciliation. The HTTP adapter authenticates and translates each
/// delivery; this core decides which Discussion or checkout to observe, coalesces pending work and owns registration.
/// </summary>
public sealed class ControllerWebhooks(string repository, AppIdentity identity, Bridge bridge, IWebhookRegistration registration,
    Uri endpoint, int port, TimeSpan interval, TimeProvider clock, IControllerLog log, CheckoutRefresh? refresh = null) : IDisposable
{
    readonly Channel<int> work = Channel.CreateUnbounded<int>(new UnboundedChannelOptions { SingleReader = true });
    readonly HashSet<int> queued = [];
    readonly CancellationTokenSource stop = new();

    /// <summary>Stops in-flight intake reads when this driving loop stops, without cancelling entered agent activity.</summary>
    public CancellationToken Stopping => stop.Token;

    /// <summary>Registers the webhook before reconciling; the HTTP listener must already accept GitHub's registration ping.</summary>
    public async Task Run(CancellationToken stoppingToken, CancellationToken activityToken)
    {
        using var stopping = stoppingToken.UnsafeRegister(_ => stop.Cancel(), null);
        Task worker = Task.CompletedTask, reconcile = Task.CompletedTask;
        try
        {
            await registration.Configure(endpoint, refresh is not null, stoppingToken);
            log.Write("mode", $"webhook-fixed public-url={endpoint} port={port} reconcile-seconds={interval.TotalSeconds:F0}");
            log.Write("ready", $"repository={repository} actor={identity.AgentLogin} mode=webhook-fixed");
            Schedule(0);
            worker = Observe(stop.Token, activityToken);
            reconcile = Reconcile(stop.Token);
            var completed = await Task.WhenAny(worker, reconcile);
            await completed;
        }
        finally
        {
            await stop.CancelAsync();
            await registration.Deactivate();
            work.Writer.TryComplete();
            try { await Task.WhenAll(worker, reconcile); }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }
    }

    /// <summary>Admits an authenticated notification without reading a repository or starting an agent turn inline.</summary>
    public WebhookDisposition Accept(WebhookObservation observation)
    {
        if (!string.Equals(observation.Repository, repository, StringComparison.OrdinalIgnoreCase))
        {
            log.Write("webhook-rejected", "reason=repository");
            return WebhookDisposition.WrongRepository;
        }
        if (observation.DeliveryId is not { } delivery) return WebhookDisposition.Invalid;
        if (observation.Change == WebhookChange.Ping)
        {
            log.Write("webhook-ping", $"delivery={delivery}");
            return WebhookDisposition.Ignored;
        }
        if (observation.Change == WebhookChange.Push)
        {
            if (refresh is null) return WebhookDisposition.Ignored;
            if (observation.RepositoryId != refresh.RepositoryId) return WebhookDisposition.WrongRepository;
            if (observation.PushedRef is not { Length: > 0 and <= 1024 } pushedRef || pushedRef.Any(char.IsControl))
                return WebhookDisposition.Invalid;
            if (pushedRef != refresh.DefaultRef) return WebhookDisposition.Ignored;
            refresh.Request();
            log.Write("webhook-delivery", $"delivery={delivery} event=push scheduled=True");
            return WebhookDisposition.Accepted;
        }
        if (observation.Change is not (WebhookChange.Discussion or WebhookChange.Comment)) return WebhookDisposition.Ignored;
        if (observation.Action.Length is 0 or > 64 || observation.Action.Any(c => !char.IsAsciiLetterLower(c) && c != '_'))
            return WebhookDisposition.Invalid;
        if (observation.DiscussionNumber is not > 0) return WebhookDisposition.Invalid;
        var number = observation.DiscussionNumber.Value;
        if (identity.IsDashboard(observation.DiscussionAuthor, observation.DiscussionBody)) return WebhookDisposition.Ignored;
        if (observation.Change == WebhookChange.Comment && identity.IsAgentActor(observation.Sender))
        {
            log.Write("webhook-ignored", $"delivery={delivery} discussion={number} reason=own-comment");
            return WebhookDisposition.Ignored;
        }
        if (!Schedule(number)) return WebhookDisposition.Unavailable;
        var name = observation.Change == WebhookChange.Comment ? "discussion_comment" : "discussion";
        log.Write("webhook-delivery", $"delivery={delivery} event={name} action={observation.Action} discussion={number} scheduled=True");
        return WebhookDisposition.Accepted;
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

    async Task Observe(CancellationToken stopping, CancellationToken activity)
    {
        await foreach (var number in work.Reader.ReadAllAsync(stopping))
        {
            stopping.ThrowIfCancellationRequested();
            lock (queued) queued.Remove(number);
            var started = clock.GetTimestamp();
            var succeeded = number == 0 ? await bridge.Poll(activity) : await bridge.Observe(number, activity);
            log.Write(number == 0 ? "reconciliation-completed" : "webhook-observed",
                $"discussion={(number == 0 ? "all" : number.ToString(CultureInfo.InvariantCulture))} success={succeeded} elapsed-ms={clock.GetElapsedTime(started).TotalMilliseconds:F0}");
        }
    }

    async Task Reconcile(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(interval, clock);
        while (await timer.WaitForNextTickAsync(ct)) Schedule(0);
    }

    public void Dispose() => stop.Dispose();
}
