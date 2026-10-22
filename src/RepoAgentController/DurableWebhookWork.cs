using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Azure;
using Azure.Identity;
using Azure.Storage.Queues;

namespace RepoAgentController;

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

    string Body(int discussion) => Json.Object(("version", 1), ("binding", binding), ("discussion", discussion)).ToJsonString();
    static IOException Failure(string operation, Exception error) => new($"Durable work {operation} failed" +
        (error is RequestFailedException azure ? $" (HTTP {azure.Status})" : "") + "; preserve queued work and inspect the queue");

    public async Task Start(CancellationToken ct)
    {
        try
        {
            // The startup itself may clone, refresh, resume and admit work after the HTTP trigger ends.
            var sent = (await queue.SendMessageAsync(Body(0), Visibility, TimeSpan.FromSeconds(-1), ct)).Value;
            await capacity.WaitAsync(ct);
            startup = new(sent.MessageId, sent.PopReceipt, 0);
            lock (sync) held.Add(startup);
            renewal = Renew();
        }
        catch (Exception error) when (error is not OperationCanceledException) { throw Failure("startup", error); }
    }

    public async Task Enqueue(int discussion, CancellationToken ct)
    {
        try { await queue.SendMessageAsync(Body(discussion), TimeSpan.Zero, TimeSpan.FromSeconds(-1), ct); }
        catch (Exception error) when (error is not OperationCanceledException) { throw Failure("enqueue", error); }
    }

    public sealed class Lease(string id, string receipt, int discussion)
    {
        public string Id { get; } = id;
        public string Receipt { get; set; } = receipt;
        public int Discussion { get; } = discussion;
        public bool Observed { get; set; }
        public bool Deleted { get; set; }
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
                    var lease = new Lease(message.MessageId, message.PopReceipt, number);
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

    public Task<bool> CompleteAtIdle(CheckoutRefresh refresh, CancellationToken ct) => refresh.CommitAtIdle(async token =>
    {
        Lease[] ready;
        lock (sync) ready = held.Where(item => item.Observed).ToArray();
        foreach (var lease in ready)
        {
            await lease.Exclusion.WaitAsync(token);
            try
            {
                await queue.DeleteMessageAsync(lease.Id, lease.Receipt, token);
                lease.Deleted = true;
                lock (sync) held.Remove(lease);
                capacity.Release();
            }
            catch (Exception error) when (error is not OperationCanceledException) { throw Failure("completion", error); }
            finally { lease.Exclusion.Release(); }
        }
    }, ct);

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
                        if (!lease.Deleted)
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
