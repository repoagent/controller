using Azure.Core.Pipeline;
using Azure.Storage;
using Azure.Storage.Queues;
using RepoAgentController;

// Test composition root: real Azurite/SDK; production CLI has no development credential fallback.
var options = new QueueClientOptions
{
    MessageEncoding = QueueMessageEncoding.Base64,
    Transport = new HttpClientTransport(new HttpClient(new HttpClientHandler { UseProxy = false }))
};
options.Retry.MaxRetries = 0;
options.Retry.NetworkTimeout = TimeSpan.FromSeconds(3);
var queue = new QueueClient(new Uri(Environment.GetEnvironmentVariable("CONTROLLER_TEST_QUEUE_ENDPOINT")!),
    new StorageSharedKeyCredential("devstoreaccount1", "Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw=="), options);
if (args is ["queue", var operation])
{
    switch (operation)
    {
        case "create": await queue.CreateAsync(); break;
        case "count": Console.WriteLine((await queue.GetPropertiesAsync()).Value.ApproximateMessagesCount); break;
        case "clear": await queue.ClearMessagesAsync(); break;
        case "peek": Console.WriteLine((await queue.PeekMessagesAsync(32)).Value.Length); break;
        case "foreign": await queue.SendMessageAsync("{\"version\":1,\"binding\":\"foreign-controller\",\"discussion\":1}"); break;
        default: throw new ArgumentException("Unknown test queue operation");
    }
    return 0;
}
return await BridgeCommand.Run(args, workQueue: args.Contains("--azure-work-queue") ? queue : null);
