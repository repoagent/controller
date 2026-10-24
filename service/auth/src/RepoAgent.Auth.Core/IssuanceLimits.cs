namespace RepoAgent.Auth;

// Optional overrides can only lower the reviewed ceilings. They cannot add quota partitions.
public sealed record IssuanceLimits(int RepositoryAdmissions = 12, int GlobalAdmissions = 120,
    int RepositoryReads = 60, int GlobalReads = 600, int RepositorySigns = 2, int GlobalSigns = 20,
    int RepositoryMints = 2, int GlobalMints = 20, int RepositoryAttempts = 8, int GlobalAttempts = 128);
