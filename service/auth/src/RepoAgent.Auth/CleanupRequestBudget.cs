using Azure.Core;
using Azure.Core.Pipeline;

namespace RepoAgent.Auth;

// Count SDK dispatches, including bearer-auth reissues; HTTP may replay below this boundary.
internal sealed class CleanupRequestBudget : HttpPipelineTransport, IDisposable
{
    private readonly HttpClientTransport inner = new(new SocketsHttpHandler
    {
        UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
        Credentials = null, DefaultProxyCredentials = null, PreAuthenticate = false
    });
    private readonly object sync = new();
    internal int SdkDispatches { get; private set; }
    internal int SdkBatchDispatches { get; private set; }
    internal bool Exceeded { get; private set; }
    internal bool CanStart(int dispatches, int batches) => SdkDispatches + dispatches <= 128 && SdkBatchDispatches + batches <= 32;
    public void Dispose() => inner.Dispose();
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
            SdkDispatches++;
            if (batch) SdkBatchDispatches++;
        }
        await inner.ProcessAsync(message);
    }
}
