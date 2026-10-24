using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Azure;
using Azure.Identity;
using Azure.Storage.Queues;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("QueueHost")]

namespace RepoAgentController;

// The owner supplies the native journal/exclusion boundary; this module owns queue candidates.
delegate Task<bool> NativeIdleCommit(Func<CancellationToken, Task> commit, CancellationToken ct);

abstract record DurableWorkHint
{
    public abstract int ToWire();
    public sealed record StartupReconcile : DurableWorkHint
    { public override int ToWire() => 0; public override string ToString() => "reconcile"; }
    public sealed record CheckoutRefresh : DurableWorkHint
    { public override int ToWire() => -1; public override string ToString() => "refresh"; }
    public sealed record Discussion(int Number) : DurableWorkHint
    {
        public override int ToWire() => Number > 0 ? Number : throw new ArgumentOutOfRangeException(nameof(Number));
        public override string ToString() => $"discussion:{Number}";
    }
    public static DurableWorkHint FromWire(int number) => number switch
    { -1 => new CheckoutRefresh(), 0 => new StartupReconcile(), > 0 => new Discussion(number), _ => throw new IOException("Invalid durable work hint") };
}

/// <summary>Retains nonsecret wake hints until observed work reaches a durable native idle boundary.</summary>
sealed class DurableWebhookWork : IAsyncDisposable
{
    static readonly TimeSpan Visibility = TimeSpan.FromSeconds(90);
    readonly QueueClient queue;
    readonly string binding;
    readonly object sync = new();
    readonly List<Lease> held = [];
    readonly CancellationTokenSource stop = new(), lost = new();
    readonly SemaphoreSlim capacity = new(32);
    Task renewal = Task.CompletedTask;
    Lease? startup;
    public CancellationToken Lost => lost.Token;

    public DurableWebhookWork(QueueClient queue, string repository, long repositoryId, string appId, long installationId, Uri broker)
    {
        this.queue = queue;
        binding = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Json.Object(
            ("repository", repository.ToLowerInvariant()), ("repositoryId", repositoryId), ("appId", appId),
            ("installationId", installationId), ("broker", broker.AbsoluteUri)).ToJsonString())));
    }

    public static QueueClient CreateClient(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !System.Text.RegularExpressions.Regex.IsMatch(endpoint, @"\Ahttps://[a-z0-9]{3,24}\.queue\.core\.windows\.net/[a-z0-9](?:[a-z0-9]|-(?!-)){1,61}[a-z0-9]\z"))
            throw new ArgumentException("--azure-work-queue must be a public Azure HTTPS queue endpoint without credentials, query or fragment");
        var options = new QueueClientOptions { MessageEncoding = QueueMessageEncoding.Base64 };
        options.Retry.MaxRetries = 0;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(3);
        var credential = new ManagedIdentityCredential(new ManagedIdentityCredentialOptions(ManagedIdentityId.SystemAssigned)
        { Retry = { MaxRetries = 0, NetworkTimeout = TimeSpan.FromSeconds(3) } });
        return new QueueClient(uri, credential, options);
    }

    string Body(DurableWorkHint hint) => Json.Object(("version", 1), ("binding", binding), ("discussion", hint.ToWire())).ToJsonString();
    static IOException Failure(string operation, Exception error) => new($"Durable work {operation} failed" +
        (error is RequestFailedException azure ? $" (HTTP {azure.Status})" : "") + "; preserve queued work and inspect the queue");

    public async Task Start(CancellationToken ct)
    {
        try
        {
            // The startup itself may clone, refresh, resume and admit work after the HTTP trigger ends.
            var hint = new DurableWorkHint.StartupReconcile();
            var sent = (await queue.SendMessageAsync(Body(hint), Visibility, TimeSpan.FromSeconds(-1), ct)).Value;
            await capacity.WaitAsync(ct);
            startup = new(sent.MessageId, sent.PopReceipt, hint);
            lock (sync) held.Add(startup);
            renewal = Renew();
        }
        catch (Exception error) when (error is not OperationCanceledException) { throw Failure("startup", error); }
    }

    public async Task Enqueue(DurableWorkHint hint, CancellationToken ct)
    {
        try { await queue.SendMessageAsync(Body(hint), TimeSpan.Zero, TimeSpan.FromSeconds(-1), ct); }
        catch (Exception error)
        {
            // A lost response or cancelled send may already have persisted a hint. Stop all native work.
            await lost.CancelAsync();
            if (error is OperationCanceledException) throw;
            throw Failure("enqueue", error);
        }
    }

    public sealed class Lease(string id, string receipt, DurableWorkHint hint, long attempt = 1)
    {
        public string Id { get; } = id;
        public string Receipt { get; set; } = receipt;
        public DurableWorkHint Hint { get; } = hint;
        public long Attempt { get; } = attempt;
        public bool Observed { get; set; }
        public bool Released { get; set; } // Deleted, or returned to the queue for retry; no longer renewed.
        public SemaphoreSlim Exclusion { get; } = new(1);
    }

    public async Task<Lease> Receive(CancellationToken ct)
    {
        if (startup is not null) { var first = startup; startup = null; return first; }
        while (true)
        {
            await capacity.WaitAsync(ct);
            var retained = false;
            try
            {
                var messages = (await queue.ReceiveMessagesAsync(1, Visibility, ct)).Value;
                if (messages.Length != 0)
                {
                    var message = messages[0];
                    var body = JsonNode.Parse(message.Body.ToString()) as JsonObject;
                    if (body is null || body.Count != 3 || body["version"]?.GetValue<int>() != 1 || body.Text("binding") != binding ||
                        body["discussion"] is not JsonValue value || !value.TryGetValue<int>(out var number) || number < -1)
                        throw new IOException("Durable work binding or format differs; preserve the queue for manual inspection");
                    var lease = new Lease(message.MessageId, message.PopReceipt, DurableWorkHint.FromWire(number), message.DequeueCount);
                    lock (sync) held.Add(lease);
                    retained = true;
                    return lease;
                }
            }
            catch (Exception error) when (error is not OperationCanceledException) { throw Failure("receive", error); }
            finally { if (!retained) capacity.Release(); }
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
    }

    public void Observed(Lease lease) { lock (sync) lease.Observed = true; }

    /// <summary>Returns unobserved work to the queue, invisible for a capped exponential backoff; it is never acknowledged.</summary>
    public async Task Retry(Lease lease, CancellationToken ct)
    {
        // The service's dequeue count makes the backoff survive restarts: 5 seconds doubling to 10 minutes.
        var delay = RetryDelay.Doubling(TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(10), (int)Math.Clamp(lease.Attempt, 1, 17));
        await lease.Exclusion.WaitAsync(ct);
        try
        {
            try { await queue.UpdateMessageAsync(lease.Id, lease.Receipt, visibilityTimeout: delay, cancellationToken: ct); }
            catch (Exception error) when (error is not OperationCanceledException) { throw Failure("retry", error); }
            lease.Released = true;
            lock (sync) held.Remove(lease);
            capacity.Release();
        }
        finally { lease.Exclusion.Release(); }
        Log.Write("durable-work-retry", $"hint={lease.Hint} attempt={lease.Attempt} retry-seconds={delay.TotalSeconds:F0}");
    }

    public Task<bool> CompleteAtIdle(NativeIdleCommit commitAtIdle, CancellationToken ct)
    {
        // Freeze candidates before validating native/refresh idle. A later push retains its own message.
        Lease[] ready;
        lock (sync) ready = held.Where(item => item.Observed).ToArray();
        return commitAtIdle(async token =>
        {
            foreach (var lease in ready)
            {
                await lease.Exclusion.WaitAsync(token);
                try
                {
                    await queue.DeleteMessageAsync(lease.Id, lease.Receipt, token);
                    lease.Released = true;
                    lock (sync) held.Remove(lease);
                    capacity.Release();
                }
                catch (Exception error) when (error is not OperationCanceledException) { throw Failure("completion", error); }
                finally { lease.Exclusion.Release(); }
            }
        }, ct);
    }

    async Task Renew()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15));
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                Lease[] snapshot;
                lock (sync) snapshot = held.ToArray();
                await Task.WhenAll(snapshot.Select(async lease =>
                {
                    await lease.Exclusion.WaitAsync(stop.Token);
                    try
                    {
                        if (!lease.Released)
                            lease.Receipt = (await queue.UpdateMessageAsync(lease.Id, lease.Receipt, visibilityTimeout: Visibility, cancellationToken: stop.Token)).Value.PopReceipt;
                    }
                    finally { lease.Exclusion.Release(); }
                }));
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (Exception error)
        {
            Log.Write("durable-work-lost", error is RequestFailedException azure ? $"Queue lease renewal failed (HTTP {azure.Status}); native work is stopping" : "Queue lease renewal failed; native work is stopping");
            await lost.CancelAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync(); await renewal;
        // Uncompleted messages stay durable. Shutdown never acknowledges uncertain work.
        stop.Dispose(); lost.Dispose(); capacity.Dispose();
    }
}
