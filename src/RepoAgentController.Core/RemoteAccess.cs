namespace RepoAgentController;

// Supplied only by a trusted driving adapter after authenticating the local caller.
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1711", Justification = "These are trusted operation permissions, not legacy code-access-security permissions.")]
public enum LocalCallerPermission { Unknown, Inspection, Administration }
public enum RemoteOperation { Unknown, Enable, Pair, Status, Clients, Revoke, Disable }
public enum RemoteAccessOutcome { Forbidden, Unavailable }
public sealed record RemoteAccessResult(RemoteAccessOutcome Outcome, string Message);

/// <summary>Fails closed until agent-tool isolation and native command admission are qualified.</summary>
public static class RemoteAccess
{
    public static RemoteAccessResult Execute(RemoteOperation operation, LocalCallerPermission permission)
    {
        if (permission != LocalCallerPermission.Administration)
            return new(RemoteAccessOutcome.Forbidden, "Remote administration permission is required.");
        if (operation is not (RemoteOperation.Enable or RemoteOperation.Pair or RemoteOperation.Status or
                RemoteOperation.Clients or RemoteOperation.Revoke or RemoteOperation.Disable))
            return new(RemoteAccessOutcome.Forbidden, "Remote operation is not authorized.");
        // No provider/state dependency: refusal cannot enable, pair, persist intent or confirm revocation.
        return new(RemoteAccessOutcome.Unavailable, "Native remote access is unavailable: agent-tool isolation and controller admission of native remote commands are not qualified. Current access and revocation are unconfirmed.");
    }
}
