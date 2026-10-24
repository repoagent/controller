using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace RepoAgentController;

/// <summary>
/// The controller's HTTP surface: Kestrel on the slim ASP.NET Core builder (ADR 0009). The controller's command line owns
/// configuration, logging and process signals, so the host reads no configuration files or variables, writes no framework
/// log and stops only when the controller stops it.
/// </summary>
sealed class ControllerHost : IAsyncDisposable
{
    public const string WebhookPath = "/github/webhook";
    readonly WebApplication app;

    ControllerHost(WebApplication app) => this.app = app;

    /// <summary>Serves the repository webhook receiver at POST <see cref="WebhookPath"/> on every interface.</summary>
    public static async Task<ControllerHost> Start(int webhookPort, RequestDelegate webhooks, CancellationToken ct)
    {
        // The content root is the installed controller, never the agent's workspace.
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IHostLifetime, ControlledLifetime>();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.ListenAnyIP(webhookPort);
        });
        var app = builder.Build();
        // Routing answers other paths with 404 and other methods with 405.
        app.MapPost(WebhookPath, webhooks);
        try { await app.StartAsync(ct); }
        catch
        {
            await app.DisposeAsync();
            throw;
        }
        return new ControllerHost(app);
    }

    /// <summary>Stops accepting connections and gives in-flight requests a bounded time to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try { await app.StopAsync(drain.Token); }
        finally { await app.DisposeAsync(); }
    }

    /// <summary>Replaces the console lifetime: process signals reach the controller's command line, not the host.</summary>
    sealed class ControlledLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
