namespace RepoAgentController;

/// <summary>
/// The managed checkout's exclusion of agent work: each delivery is admitted before it reaches a session, so a checkout refresh
/// waits until no admitted work, including work whose outcome is uncertain, can still run. Admission fails while refresh is
/// blocked or too much work is unresolved.
/// </summary>
public interface ICheckoutAdmission
{
    /// <summary>Admits one delivery, first applying any due refresh at the global idle boundary.</summary>
    Task<ICheckoutTurn> Admit(CancellationToken ct);

    /// <summary>
    /// Applies a change only while all admitted checkout activity is settled and no refresh is outstanding. An admission
    /// that cannot establish global idle refuses the change; it never assumes that no running turn means no tool activity.
    /// </summary>
    Task<bool> CommitAtIdle(Func<CancellationToken, Task> commit, CancellationToken ct) => Task.FromResult(false);
}

/// <summary>
/// One admitted delivery, resolved exactly once: confirmed with the turn the agent acknowledged, abandoned when no turn can hold
/// its input, or left unconfirmed when the agent may hold it unacknowledged. Disposing an unresolved one blocks refresh.
/// </summary>
public interface ICheckoutTurn : IDisposable
{
    /// <summary>The session the delivery goes to, once it exists.</summary>
    void Bind(string session);

    /// <summary>The agent acknowledged <paramref name="turn"/> on <paramref name="session"/>.</summary>
    void Accepted(string session, string turn);

    /// <summary>No turn holds the input: nothing was offered, or the agent refused it.</summary>
    void Abandon();

    /// <summary>The agent may hold the input without acknowledgement, so the checkout stays excluded until the session settles.</summary>
    void Unconfirmed();

    /// <summary>The acknowledged turn holds the input.</summary>
    void Confirm();
}
