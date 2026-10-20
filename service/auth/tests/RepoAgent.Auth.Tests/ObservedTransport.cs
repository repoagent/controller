using Azure.Core;
using Azure.Core.Pipeline;

namespace RepoAgent.Auth.Tests;

// Faults surround real HTTP; no Table response or transaction is emulated here.
internal sealed class ObservedTransport : HttpPipelineTransport
{
    private readonly HttpClientTransport inner = new();
    public Func<HttpMessage, Task>? Before { get; init; }
    public Func<HttpMessage, Task>? After { get; init; }
    public int Calls;
    public override Request CreateRequest() => inner.CreateRequest();
    public override void Process(HttpMessage message) => ProcessAsync(message).AsTask().GetAwaiter().GetResult();
    public override async ValueTask ProcessAsync(HttpMessage message)
    {
        Interlocked.Increment(ref Calls);
        if (Before is not null) await Before(message);
        await inner.ProcessAsync(message);
        if (After is not null) await After(message);
    }
    public static bool IsBatch(HttpMessage message) => message.Request.Uri.ToUri().AbsolutePath.EndsWith("/$batch", StringComparison.Ordinal);
}
