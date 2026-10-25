namespace RepoAgentController;

/// <summary>
/// The managed checkout's exclusion of native work: each delivery is admitted before it reaches a session, so a checkout refresh
/// waits until no admitted work, including work whose outcome is uncertain, can still run. Admission fails while refresh is
/// blocked or too much work is unresolved.
/// </summary>
public interface ICheckoutAdmission
{
    /// <summary>Admits one delivery, first applying any due refresh at the global idle boundary.</summary>
    Task<ICheckoutTurn> Admit(CancellationToken ct);
}

/// <summary>
/// One admitted delivery, resolved exactly once: confirmed with the turn the native acknowledged, abandoned when no turn can hold
/// its input, or left unconfirmed when the native may hold it unacknowledged. Disposing an unresolved one blocks refresh.
/// </summary>
public interface ICheckoutTurn : IDisposable
{
    /// <summary>The session the delivery goes to, once it exists.</summary>
    void Bind(string session);

    /// <summary>The native acknowledged <paramref name="turn"/> on <paramref name="session"/>.</summary>
    void Accepted(string session, string turn);

    /// <summary>No turn holds the input: nothing was offered, or the native refused it.</summary>
    void Abandon();

    /// <summary>The native may hold the input without acknowledgement, so the checkout stays excluded until the session settles.</summary>
    void Unconfirmed();

    /// <summary>The acknowledged turn holds the input.</summary>
    void Confirm();
}
