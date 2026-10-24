using Azure.Core.Pipeline;
using Azure.Core;
using Azure.Storage;
using Azure.Storage.Queues;
using RepoAgentController;

// Test composition root: real Azurite/SDK; production CLI has no development credential fallback.
var options = new QueueClientOptions
{
    MessageEncoding = QueueMessageEncoding.Base64,
    Transport = new ResponseLossTransport(new HttpClientTransport(new HttpClient(new HttpClientHandler { UseProxy = false })),
        Environment.GetEnvironmentVariable("CONTROLLER_TEST_DROP_QUEUE_SEND"))
};
options.Retry.MaxRetries = 0;
options.Retry.NetworkTimeout = TimeSpan.FromSeconds(3);
var queue = new QueueClient(new Uri(Environment.GetEnvironmentVariable("CONTROLLER_TEST_QUEUE_ENDPOINT")!),
    new StorageSharedKeyCredential("devstoreaccount1", "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="), options);
if (args is ["queue", "hint", var discussion])
{
    // Work retained from an earlier process, bound exactly like an accepted fixture delivery.
    await using var work = new DurableWebhookWork(queue, "owner/project", 345, "123", 234, new Uri("https://broker.fixture"));
    await work.Enqueue(new DurableWorkHint.Discussion(int.Parse(discussion)), CancellationToken.None);
    return 0;
}
if (args is ["queue", var operation])
{
    switch (operation)
    {
        case "create": await queue.CreateAsync(); break;
        case "count": Console.WriteLine((await queue.GetPropertiesAsync()).Value.ApproximateMessagesCount); break;
        case "clear": await queue.ClearMessagesAsync(); break;
        case "peek": Console.WriteLine((await queue.PeekMessagesAsync(32)).Value.Length); break;
        case "foreign": await queue.SendMessageAsync("{\"version\":1,\"binding\":\"foreign-controller\",\"discussion\":1}"); break;
        case "idle-race":
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
            {
                var ct = deadline.Token;
                await using var work = new DurableWebhookWork(queue, "owner/project", 345, "123", 234, new Uri("https://broker.fixture"));
                await work.Start(ct);
                work.Observed(await work.Receive(ct));
                var idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var completed = work.CompleteAtIdle(async (commit, token) =>
                {
                    idle.SetResult();
                    await release.Task.WaitAsync(token);
                    await commit(token);
                    return true;
                }, ct);
                await idle.Task.WaitAsync(ct); // Native coordinator has validated idle; the queue commit is held.
                await work.Enqueue(new DurableWorkHint.CheckoutRefresh(), ct);
                work.Observed(await work.Receive(ct)); // A concurrent push has requested refresh and published its hint.
                release.SetResult();
                await completed;
                var count = (await queue.GetPropertiesAsync(cancellationToken: ct)).Value.ApproximateMessagesCount;
                Console.WriteLine($"Push pending refresh: queue count={count}; expected=1");
                if (count != 1) return 2;
            }
            break;
        default: throw new ArgumentException("Unknown test queue operation");
    }
    return 0;
}
return await BridgeCommand.Run(args, workQueue: args.Contains("--azure-work-queue") ? queue : null);

// Forward to real Azurite, then lose one real successful send response. No Queue decisions are emulated.
sealed class ResponseLossTransport(HttpPipelineTransport inner, string? marker) : HttpPipelineTransport
{
    public override Request CreateRequest() => inner.CreateRequest();
    public override void Process(HttpMessage message) { inner.Process(message); Drop(message); }
    public override async ValueTask ProcessAsync(HttpMessage message) { await inner.ProcessAsync(message); Drop(message); }
    void Drop(HttpMessage message)
    {
        if (marker is not null && File.Exists(marker) && message.Request.Method == RequestMethod.Post &&
            message.Request.Uri.Path.EndsWith("/messages", StringComparison.Ordinal) && message.Response.Status == 201)
        {
            File.Delete(marker);
            message.Response.Dispose();
            throw new IOException("Controlled loss of a real committed send response");
        }
    }
}
