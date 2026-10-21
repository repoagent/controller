using Azure.Core;
using Azure.Core.Pipeline;

namespace RepoAgent.Auth;

// Count at the transport boundary, including authentication reissues and failed sends.
internal sealed class CleanupRequestBudget : HttpPipelineTransport
{
    private readonly HttpClientTransport inner = new();
    private readonly object sync = new();
    internal int Requests { get; private set; }
    internal int Egts { get; private set; }
    internal bool Exceeded { get; private set; }
    internal bool CanStart(int requests, int egts) => Requests + requests <= 128 && Egts + egts <= 32;
    public override Request CreateRequest() => inner.CreateRequest();
    public override void Process(HttpMessage message) => ProcessAsync(message).AsTask().GetAwaiter().GetResult();
    public override async ValueTask ProcessAsync(HttpMessage message)
    {
        var batch = message.Request.Uri.ToUri().AbsolutePath.EndsWith("/$batch", StringComparison.Ordinal);
        lock (sync)
        {
            if (!CanStart(1, batch ? 1 : 0))
            {
                Exceeded = true;
                throw new InvalidOperationException("Cleanup request budget invariant exceeded.");
            }
            Requests++;
            if (batch) Egts++;
        }
        await inner.ProcessAsync(message);
    }
}
