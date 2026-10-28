namespace RepoAgent.Auth;

// Fixed diagnostic categories, never identifiers or provider text. They do not change a public response body.
public enum BrokerRejectionReason
{
    Other,
    RequestConcurrency,
    RequestMinute,
    RepositoryAttempts,
    GlobalAttempts,
    RetainedIssuance,
    RepositoryMinuteBudget,
    GlobalMinuteBudget,
    RetainedEnrollments,
    CandidateRepositories,
    RepositoryPending,
    GlobalPending,
    EnrollmentBackoff
}
