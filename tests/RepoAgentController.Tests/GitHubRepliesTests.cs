using Xunit;

namespace RepoAgentController.Tests;

// The core's decisions over what GitHub answered a controller-owned request: success, a confirmed absence, or a classified
// failure with its kind, uncertainty and earliest retry. GitHubResponseGoldenTests pins how gh output reads into these facts.
public sealed class GitHubRepliesTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    static readonly string[] Lookup = ["repository", "discussion"];

    static GitHubReply Reply(int status, string message = "", bool data = false, params GitHubReplyError[] errors) => new(status, errors, message, data);

    static GitHubReplyError Error(string type, params string[] path) => new(type, path);

    static GitHubRequestException Failure(GitHubReply? reply) => Assert.Throws<GitHubRequestException>(() => GitHubReplies.Check(reply));

    static void Classified(GitHubRequestException failure, GitHubFailure kind, bool uncertain, string message, DateTimeOffset? notBefore = null)
    {
        Assert.Equal((kind, uncertain, message), (failure.Kind, failure.Uncertain, failure.Message));
        Assert.Equal(notBefore, failure.NotBefore(Now));
    }

    [Fact]
    public void Data_without_errors_succeeds_and_a_success_without_data_is_an_uncertain_transient_failure()
    {
        GitHubReplies.Check(Reply(200, data: true));
        GitHubReplies.Check(Reply(204, data: true));
        Classified(Failure(Reply(200)), GitHubFailure.Transient, true, "HTTP 200: no data");
        Classified(Failure(null), GitHubFailure.Transient, true, "unrecognized gh response");
    }

    [Fact]
    public void An_exhausted_primary_rate_limit_waits_for_its_reset()
    {
        var reset = Now.AddMinutes(80);
        var limited = Reply(403, "API rate limit exceeded for installation ID 234.") with { RateLimitRemaining = "0", RateLimitReset = reset.ToUnixTimeSeconds().ToString() };
        Classified(Failure(limited), GitHubFailure.RateLimited, false, "HTTP 403: API rate limit exceeded for installation ID 234.", reset);

        // An exhausted limit alone marks a 403 as a rate limit, whatever GitHub's message.
        Classified(Failure(Reply(403, "x") with { RateLimitRemaining = "0", RateLimitReset = reset.ToUnixTimeSeconds().ToString() }), GitHubFailure.RateLimited, false, "HTTP 403: x", reset);
        // Only an exhausted limit sets its reset; a reset alone is no limit and no retry time.
        Classified(Failure(limited with { RateLimitReset = null }), GitHubFailure.RateLimited, false, "HTTP 403: API rate limit exceeded for installation ID 234.");
        Classified(Failure(limited with { RateLimitRemaining = "1" }), GitHubFailure.RateLimited, false, "HTTP 403: API rate limit exceeded for installation ID 234.");
        Classified(Failure(Reply(403, "x") with { RateLimitRemaining = "00", RateLimitReset = "1793020800" }), GitHubFailure.Permanent, false, "HTTP 403: x");
        // A success is never rate limited, even when its request used the last of the limit.
        GitHubReplies.Check(Reply(200, data: true) with { RateLimitRemaining = "0", RateLimitReset = "1793020800" });
    }

    [Fact]
    public void A_GraphQL_RATE_LIMITED_error_is_rate_limited_whatever_the_status()
    {
        Classified(Failure(Reply(200, "API rate limit exceeded for installation.", false, Error("RATE_LIMITED"))), GitHubFailure.RateLimited, false,
            "HTTP 200 RATE_LIMITED: API rate limit exceeded for installation.");
        Classified(Failure(Reply(200, "a", true, Error("FORBIDDEN"), Error("RATE_LIMITED"))), GitHubFailure.RateLimited, false, "HTTP 200 FORBIDDEN: a");
    }

    [Fact]
    public void HTTP_429_is_rate_limited_until_its_Retry_After()
    {
        Classified(Failure(Reply(429, "Too many requests") with { RetryAfter = "30" }), GitHubFailure.RateLimited, false, "HTTP 429: Too many requests", Now.AddSeconds(30));
        Classified(Failure(Reply(429)), GitHubFailure.RateLimited, false, "HTTP 429");
    }

    [Fact]
    public void A_403_secondary_rate_limit_is_rate_limited_by_its_Retry_After_or_its_message()
    {
        var secondary = Reply(403, "You have exceeded a secondary rate limit. Please wait a few minutes before you try again.");
        Classified(Failure(secondary with { RetryAfter = "60" }), GitHubFailure.RateLimited, false, "HTTP 403: " + secondary.Message, Now.AddMinutes(1));
        Classified(Failure(secondary), GitHubFailure.RateLimited, false, "HTTP 403: " + secondary.Message);
        Classified(Failure(Reply(403, "You have triggered an Abuse detection mechanism.")), GitHubFailure.RateLimited, false, "HTTP 403: You have triggered an Abuse detection mechanism.");
        // A Retry-After alone marks a 403 as a rate limit; the later of it and an exhausted limit's reset applies.
        Classified(Failure(Reply(403, "x") with { RetryAfter = "0" }), GitHubFailure.RateLimited, false, "HTTP 403: x", Now);
        Classified(Failure(Reply(403, "x") with { RetryAfter = "7200", RateLimitRemaining = "0", RateLimitReset = Now.AddMinutes(80).ToUnixTimeSeconds().ToString() }),
            GitHubFailure.RateLimited, false, "HTTP 403: x", Now.AddHours(2));
        // An unusable Retry-After is no signal, so an ordinary 403 is a permanent refusal.
        Classified(Failure(Reply(403, "x") with { RetryAfter = "-1" }), GitHubFailure.Permanent, false, "HTTP 403: x");
        Classified(Failure(Reply(403, "x") with { RetryAfter = "Wed, 21 Oct 2026 07:28:00 GMT" }), GitHubFailure.Permanent, false, "HTTP 403: x");
        Classified(Failure(Reply(403, "Resource not accessible by integration")), GitHubFailure.Permanent, false, "HTTP 403: Resource not accessible by integration");
    }

    [Fact]
    public void FORBIDDEN_on_addReaction_is_a_permanent_refusal_and_only_rejection_types_are_permanent()
    {
        Classified(Failure(Reply(200, "Resource not accessible by integration", true, Error("FORBIDDEN", "addReaction"))), GitHubFailure.Permanent, false,
            "HTTP 200 FORBIDDEN: Resource not accessible by integration");
        Classified(Failure(Reply(200, "a", false, Error("NOT_FOUND"), Error("UNPROCESSABLE"))), GitHubFailure.Permanent, false, "HTTP 200 NOT_FOUND: a");
        // Any other or missing type may mean GitHub applied part of the request.
        Classified(Failure(Reply(200, "a", false, Error("FORBIDDEN"), Error("INTERNAL"))), GitHubFailure.Transient, true, "HTTP 200 FORBIDDEN: a");
        Classified(Failure(Reply(200, "a", false, Error(""), Error("FORBIDDEN"))), GitHubFailure.Transient, true, "HTTP 200 FORBIDDEN: a");
        Classified(Failure(Reply(200, "", false, Error(""))), GitHubFailure.Transient, true, "HTTP 200");
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    [InlineData(408)]
    [InlineData(409)]
    [InlineData(304)]
    public void A_server_error_or_a_conflict_is_transient_and_may_have_been_applied(int status) =>
        Classified(Failure(Reply(status, "Server Error")), GitHubFailure.Transient, true, $"HTTP {status}: Server Error");

    [Theory]
    [InlineData(400, GitHubFailure.Permanent)]
    [InlineData(404, GitHubFailure.Permanent)]
    [InlineData(410, GitHubFailure.Permanent)]
    [InlineData(422, GitHubFailure.Permanent)]
    [InlineData(401, GitHubFailure.Transient)]
    public void A_refusal_was_not_applied_and_only_bad_credentials_may_recover(int status, GitHubFailure kind) =>
        Classified(Failure(Reply(status, "refused")), kind, false, $"HTTP {status}: refused");

    [Fact]
    public void Only_the_typed_NOT_FOUND_for_the_looked_up_field_inside_its_resolved_parent_confirms_absence()
    {
        var deleted = Reply(200, "Could not resolve to a Discussion with the number of 1.", true, Error("NOT_FOUND", Lookup)) with { Field = ReplyField.Null };
        Assert.True(GitHubReplies.ConfirmsAbsent(deleted, Lookup));
        Assert.False(GitHubReplies.Found(deleted, Lookup, "Discussion"));

        Assert.False(GitHubReplies.ConfirmsAbsent(deleted with { Status = 404 }, Lookup));
        Assert.False(GitHubReplies.ConfirmsAbsent(deleted with { Field = ReplyField.Missing }, Lookup));
        Assert.False(GitHubReplies.ConfirmsAbsent(deleted with { Field = ReplyField.Value }, Lookup));
        Assert.False(GitHubReplies.ConfirmsAbsent(deleted with { Errors = [Error("NOT_FOUND", "repository", "discussion", "author")] }, Lookup));
        Assert.False(GitHubReplies.ConfirmsAbsent(deleted with { Errors = [new("NOT_FOUND", ["repository", null])] }, Lookup));
        Assert.False(GitHubReplies.ConfirmsAbsent(deleted with { Errors = [Error("NOT_FOUND")] }, Lookup));
        Assert.False(GitHubReplies.ConfirmsAbsent(deleted with { Errors = [Error("NOT_FOUND", Lookup), Error("NOT_FOUND", Lookup)] }, Lookup));
        Assert.False(GitHubReplies.ConfirmsAbsent(null, Lookup));
    }

    // The six lookup answers that are no deletion: each keeps the Discussion's state as a failed observation.
    [Fact]
    public void A_lookup_GitHub_did_not_confirm_absent_is_a_classified_failure()
    {
        Classified(Assert.Throws<GitHubRequestException>(() => GitHubReplies.Found(Reply(200, "Could not resolve to a Repository with the name 'owner/project'.", true,
            Error("NOT_FOUND", "repository")), Lookup, "Discussion")), GitHubFailure.Permanent, false, "HTTP 200 NOT_FOUND: Could not resolve to a Repository with the name 'owner/project'.");
        Classified(Assert.Throws<GitHubRequestException>(() => GitHubReplies.Found(Reply(200, "Resource not accessible by integration", true,
            Error("FORBIDDEN", Lookup)) with { Field = ReplyField.Null }, Lookup, "Discussion")), GitHubFailure.Permanent, false, "HTTP 200 FORBIDDEN: Resource not accessible by integration");
        Classified(Assert.Throws<GitHubRequestException>(() => GitHubReplies.Found(Reply(200, "API rate limit exceeded for installation.", false,
            Error("RATE_LIMITED")), Lookup, "Discussion")), GitHubFailure.RateLimited, false, "HTTP 200 RATE_LIMITED: API rate limit exceeded for installation.");
        Classified(Assert.Throws<GitHubRequestException>(() => GitHubReplies.Found(Reply(200, "Could not resolve to a Discussion with the number of 1.", true,
            Error("", Lookup)) with { Field = ReplyField.Null }, Lookup, "Discussion")), GitHubFailure.Transient, true, "HTTP 200: Could not resolve to a Discussion with the number of 1.");
        Classified(Assert.Throws<GitHubRequestException>(() => GitHubReplies.Found(Reply(200, data: true) with { Field = ReplyField.Null }, Lookup, "Discussion")),
            GitHubFailure.Transient, false, "incomplete Discussion response");
        Classified(Assert.Throws<GitHubRequestException>(() => GitHubReplies.Found(Reply(502, "Server Error"), Lookup, "Discussion")),
            GitHubFailure.Transient, true, "HTTP 502: Server Error");

        Assert.True(GitHubReplies.Found(Reply(200, data: true) with { Field = ReplyField.Value }, Lookup, "Discussion"));
        Classified(Assert.Throws<GitHubRequestException>(() => GitHubReplies.Found(Reply(200, data: true), Lookup, "Discussion")),
            GitHubFailure.Transient, false, "incomplete Discussion response");
    }
}
