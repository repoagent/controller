namespace RepoAgentController;

/// <summary>Publishes this controller lifetime's current authority to its agent tools without persisting a token.</summary>
public interface IAgentToolCredentials
{
    /// <summary>
    /// Binds a tool handoff to the current usable token and rejected-token feedback. Disposing the binding stops serving
    /// tools; the callbacks remain owned by the token lifetime, and never initiate renewal for a tool request.
    /// </summary>
    IDisposable Bind(Func<GitHubToken?> current, Action<string> rejected);
}
