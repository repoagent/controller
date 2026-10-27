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
    public bool Exceeded { get; private set; }
    public bool CanStart(int dispatches, int batches) => Dispatches + dispatches <= 128 && BatchDispatches + batches <= 32;
    // The reviewed reference scope of this transport, and the most sends one counted dispatch makes in it.
    public string EnvelopeScope => "Azure.Core1.62.0/.NET11.0.0-rc.3.26522.148/direct HTTP1.x/no handler authentication";
    public int MaxSendsPerDispatch => 4;
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
            Dispatches++;
            if (batch) BatchDispatches++;
        }
        await inner.ProcessAsync(message);
    }
}
