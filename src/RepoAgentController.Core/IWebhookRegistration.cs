namespace RepoAgentController;

/// <summary>The repository webhook that delivers Discussion events to this controller, and the secret GitHub signs them with.</summary>
public interface IWebhookRegistration
{
    /// <summary>The secret GitHub signs each delivery with; the same across lifetimes.</summary>
    string Secret { get; }

    /// <summary>Registers the hook for <paramref name="endpoint"/>, or updates the saved one, and activates it. It also subscribes to
    /// pushes when <paramref name="push"/>. Throws <see cref="IOException"/> when GitHub does not confirm the hook.</summary>
    Task Configure(Uri endpoint, bool push, CancellationToken ct);

    /// <summary>Deactivates the saved hook, if any, within its own bound. False when GitHub did not confirm it; the failure is logged.</summary>
    Task<bool> Deactivate();
}
