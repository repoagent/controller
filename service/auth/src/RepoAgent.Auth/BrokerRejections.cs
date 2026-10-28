using System.Diagnostics.Metrics;

namespace RepoAgent.Auth;

// One process-lifetime meter. Only this fixed taxonomy reaches metrics; no request data is recorded.
internal static class BrokerRejections
{
    internal const string MeterName = "RepoAgent.Auth.Rejections";
    private static readonly Lazy<Meter> Meter = new(() => new(MeterName));
    private static readonly Lazy<Counter<long>> Rejected = new(() => Meter.Value.CreateCounter<long>("repoagent.broker.rejections", "{rejection}"));

    internal static void Record(BrokerRejectionReason reason)
    {
        try { Rejected.Value.Add(1, new KeyValuePair<string, object?>("reason", Label(reason))); }
        catch (Exception)
        {
            // Optional monitoring must not change a refusal, admission decision or credential state.
        }
    }

    private static string Label(BrokerRejectionReason reason) => reason switch
    {
        BrokerRejectionReason.RequestConcurrency => "request_concurrency",
        BrokerRejectionReason.RequestMinute => "request_minute",
        BrokerRejectionReason.RepositoryAttempts => "repository_attempts",
        BrokerRejectionReason.GlobalAttempts => "global_attempts",
        BrokerRejectionReason.RetainedIssuance => "retained_issuance",
        BrokerRejectionReason.RepositoryMinuteBudget => "repository_minute_budget",
        BrokerRejectionReason.GlobalMinuteBudget => "global_minute_budget",
        BrokerRejectionReason.RetainedEnrollments => "retained_enrollments",
        BrokerRejectionReason.CandidateRepositories => "candidate_repositories",
        BrokerRejectionReason.RepositoryPending => "repository_pending",
        BrokerRejectionReason.GlobalPending => "global_pending",
        BrokerRejectionReason.EnrollmentBackoff => "enrollment_backoff",
        _ => "other"
    };
}
