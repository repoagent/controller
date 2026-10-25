namespace RepoAgentController;

/// <summary>A real GitHub user used as a mention target, independently of the publishing App.</summary>
public sealed record MentionUser(string Login)
{
    /// <summary>Whether a message mentions this user, given the users its rendered text links as mentions.</summary>
    public bool IsMentioned(IReadOnlySet<string> mentionedUsers) =>
        mentionedUsers.Any(user => string.Equals(user, Login, StringComparison.OrdinalIgnoreCase));
}
