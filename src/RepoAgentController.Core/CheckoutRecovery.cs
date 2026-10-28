namespace RepoAgentController;

/// <summary>Explicit offline settlement of native uncertainty, including legacy blocks. The caller owns both controller leases and verifies checkout identity.</summary>
public static class CheckoutRecovery
{
    public static void Recover(CheckoutBinding binding, IDiscussionStore discussions, IRefreshJournal journals,
        bool runtimeAndToolsStopped, TimeProvider clock)
    {
        if (!runtimeAndToolsStopped) throw Refused("confirm that the previous controller, agent runtime and ALL agent tools have stopped");
        var journal = journals.Read() ?? throw Refused("the lifecycle journal is missing");
        var state = discussions.ReadDiscussions() ?? throw Refused("the Discussion state is missing");
        if (journal.Root != binding.Root || journal.Owner != binding.Owner || journal.RepositoryId != binding.RepositoryId ||
            journal.Completed < 0 || journal.Requested < journal.Completed || journal.Running < 0)
            throw Refused("journal binding or generations differ");
        if (!journal.Blocked || journal.Applying is not null || !AgentEvidenceReadable(journal))
            throw Refused("only valid non-applying native uncertainty is supported; retain applying or malformed evidence and use an isolated fresh instance");
        if (!CanNormalize(state))
            throw Refused("Discussion state cannot be normalized safely");

        // A snapshot must finish before either durable write. If interrupted, normalization can be retried while still fenced.
        journals.PreserveStartupEvidence();
        foreach (var item in state.Discussions.Values)
        {
            if (item.Pending is { } pending && item.Waiting is { } waiting)
            {
                if (!string.IsNullOrEmpty(item.SessionId))
                    for (var index = 0; index < pending.Inputs!.Value; index++)
                        waiting.Inputs[index] = waiting.Inputs[index] with { Redelivery = true };
                waiting.Retry = new(checked((waiting.Retry?.Attempts ?? 0) + 1), clock.GetUtcNow());
                item.Pending = null;
            }
            item.Running = null;
        }
        discussions.WriteDiscussions(state);
        journal.Active.Clear();
        journal.Running = 0;
        journal.Blocked = false;
        journal.Reason = null;
        // Last write: old pending input can no longer cause an immediate relatch on the next start.
        journals.Write(journal);
    }

    /// <summary>Whether interrupted input is readable before startup can normalize away its pending marker.</summary>
    public static bool CanNormalize(DiscussionState state) => state.Unreadable is null &&
        state.Discussions.Values.All(item => item.Unreadable is null && item.UnreadableKeys.Count == 0 &&
            (item.Pending is not { } pending || pending.AsRead is null && pending.Inputs is > 0 &&
                pending.Operation is "send" or "create-and-send" && item.Waiting is { } waiting && pending.Inputs <= waiting.Inputs.Count &&
                waiting.Retry?.AsRead is null && waiting.Inputs.All(input => input.ObservedAtAsRead is null && input.Receipt?.AsRead is null)));

    /// <summary>Native records whose identities and state are forms this controller can have written.</summary>
    public static bool AgentEvidenceReadable(RefreshJournal journal) => !journal.RunningUnreadable && !journal.ActiveUnreadable &&
        journal.Running >= 0 && journal.ReasonAsRead is null &&
        journal.Active.Values.All(item => item.AsRead is null && item.Since > 0 &&
            !(item.Confirmed && item.Unconfirmed) &&
            (item.Thread.Length == 0 ? item.Turn.Length == 0 && !item.Confirmed && !item.Unconfirmed : Valid(item.Thread)) &&
            (item.Turn.Length == 0 ? !item.Confirmed : Valid(item.Turn)));

    static bool Valid(string value) => value.Length is > 0 and <= 256 && !value.Any(char.IsControl);

    static IOException Refused(string reason) => new("Checkout recovery refused: " + reason);
}
