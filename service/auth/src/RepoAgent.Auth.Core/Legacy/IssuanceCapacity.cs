namespace RepoAgent.Auth;

// Historical keys for the read-only schema-1 authority inspector. The runtime uses schema 2.
public static class IssuanceCapacity
{
    public const string Scope = "global:issuance-v1", EnrollmentScope = "global:enrollment-v1";
}

internal interface IRetiredLedger
{
    string? RetirementState { get; set; }
    DateTimeOffset? RetirementAuditAt { get; set; }
}
