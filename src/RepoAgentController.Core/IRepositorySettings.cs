namespace RepoAgentController;

/// <summary>The repository settings an operator holds in direct repository Agent variables named <c>REPOAGENT_*</c>.</summary>
public interface IRepositorySettings
{
    /// <summary>Reads one setting's raw value; null only when GitHub confirms that the variable does not exist. Any other
    /// outcome throws: <see cref="RepositorySettingRejectedException"/> when GitHub answered with another HTTP status, and
    /// <see cref="RepositorySettingsUnreadableException"/> when the runtime authority cannot read settings at all.</summary>
    Task<string?> Read(string name, CancellationToken ct);
}

/// <summary>GitHub answered a repository setting read with an HTTP status that gives neither its value nor a confirmed absence.</summary>
public sealed class RepositorySettingRejectedException(int status) : IOException($"GitHub returned HTTP {status}")
{
    public int Status { get; } = status;
}

/// <summary>The controller's runtime authority cannot read repository settings at all, so no read is attempted.</summary>
public sealed class RepositorySettingsUnreadableException(string reason) : IOException(reason)
{
    public string Reason { get; } = reason;
}
