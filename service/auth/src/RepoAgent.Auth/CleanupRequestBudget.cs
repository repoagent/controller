using Azure.Core;
using Azure.Core.Pipeline;

namespace RepoAgent.Auth;

// Cleanup's request budget: counts SDK dispatches, including bearer-auth reissues; HTTP may replay below this boundary.
internal sealed class CleanupRequestBudget : HttpPipelineTransport, ICleanupBudget, IDisposable
{
    private readonly HttpClientTransport inner = new(new SocketsHttpHandler
    {
        UseProxy = false,
        AllowAutoRedirect = false,
        UseCookies = false,
        Credentials = null,
        DefaultProxyCredentials = null,
        PreAuthenticate = false
    });
    private readonly object sync = new();
    public int Dispatches { get; private set; }
    public int BatchDispatches { get; private set; }
    public void Dispose() => inner.Dispose();
    public override Request CreateRequest() => inner.CreateRequest();
    public override void Process(HttpMessage message) => ProcessAsync(message).AsTask().GetAwaiter().GetResult();
    public override async ValueTask ProcessAsync(HttpMessage message)
    {
        var batch = message.Request.Uri.ToUri().AbsolutePath.EndsWith("/$batch", StringComparison.Ordinal);
        lock (sync)
        {
            if (Dispatches >= 512 || (batch && BatchDispatches >= 256))
                throw new InvalidOperationException("Cleanup request budget invariant exceeded.");
            Dispatches++;
            if (batch) BatchDispatches++;
        }
        await inner.ProcessAsync(message);
    }
}
