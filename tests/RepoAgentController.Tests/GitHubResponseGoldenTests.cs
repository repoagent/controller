using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

// How a controller-owned GraphQL request reads gh --include output: GitHub's data, a confirmed absence for a Discussion lookup,
// or the classified failure with its kind, uncertainty, earliest retry and message. bcfd719's outcomes, one row per form.
public sealed class GitHubResponseGoldenTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    const string Missing = "Could not resolve to a Discussion with the number of 1.";

    static string Response(int status, string body, params string[] headers) =>
        $"HTTP/2.0 {status} {(status == 200 ? "OK" : "Status")}\r\n" + string.Concat(headers.Select(header => header + "\r\n")) + "Content-Type: application/json\r\n\r\n" + body;

    public static TheoryData<string, string, bool, string> Forms() => new()
    {
        // Controller-owned requests.
        { "data", Response(200, """{"data":{"node":{"id":"D1"}}}"""), false, """data {"node":{"id":"D1"}}""" },
        { "no data", Response(200, "{}"), false, "Transient uncertain=True notBefore=-: HTTP 200: no data" },
        { "FORBIDDEN on addReaction", Response(200, """{"data":{"addReaction":null},"errors":[{"type":"FORBIDDEN","path":["addReaction"],"message":"Resource not accessible by integration"}]}"""), false,
            "Permanent uncertain=False notBefore=-: HTTP 200 FORBIDDEN: Resource not accessible by integration" },
        { "NOT_FOUND and UNPROCESSABLE", Response(200, """{"errors":[{"type":"NOT_FOUND","message":"a"},{"type":"UNPROCESSABLE","message":"b"}]}"""), false,
            "Permanent uncertain=False notBefore=-: HTTP 200 NOT_FOUND: a" },
        { "rejection and other type", Response(200, """{"errors":[{"type":"FORBIDDEN","message":"a"},{"type":"INTERNAL","message":"b"}]}"""), false,
            "Transient uncertain=True notBefore=-: HTTP 200 FORBIDDEN: a" },
        { "untyped error", Response(200, """{"errors":[{"message":"Something went wrong"}]}"""), false, "Transient uncertain=True notBefore=-: HTTP 200: Something went wrong" },
        { "untyped then typed", Response(200, """{"errors":[{"message":"a"},{"type":"FORBIDDEN","message":"b"}]}"""), false, "Transient uncertain=True notBefore=-: HTTP 200 FORBIDDEN: a" },
        { "GraphQL RATE_LIMITED", Response(200, """{"errors":[{"type":"RATE_LIMITED","message":"API rate limit exceeded for installation."}]}"""), false,
            "RateLimited uncertain=False notBefore=-: HTTP 200 RATE_LIMITED: API rate limit exceeded for installation." },
        { "primary rate limit", Response(403, """{"message":"API rate limit exceeded for installation ID 234."}""", "X-RateLimit-Remaining: 0", "X-RateLimit-Reset: 1793020800"), false,
            "RateLimited uncertain=False notBefore=2026-10-26T13:20:00.0000000+00:00: HTTP 403: API rate limit exceeded for installation ID 234." },
        { "primary rate limit on 200", Response(200, """{"data":{"x":1}}""", "X-RateLimit-Remaining: 0", "X-RateLimit-Reset: 1793020800"), false,
            """data {"x":1}""" },
        { "remaining header lower case", Response(403, """{"message":"x"}""", "x-ratelimit-remaining: 0", "x-ratelimit-reset: 1793020800"), false,
            "RateLimited uncertain=False notBefore=2026-10-26T13:20:00.0000000+00:00: HTTP 403: x" },
        { "remaining 00", Response(403, """{"message":"x"}""", "X-RateLimit-Remaining: 00", "X-RateLimit-Reset: 1793020800"), false, "Permanent uncertain=False notBefore=-: HTTP 403: x" },
        { "exhausted without reset", Response(403, """{"message":"x"}""", "X-RateLimit-Remaining: 0"), false, "RateLimited uncertain=False notBefore=-: HTTP 403: x" },
        { "reset without exhaustion", Response(403, """{"message":"x"}""", "X-RateLimit-Remaining: 5", "X-RateLimit-Reset: 1793020800"), false, "Permanent uncertain=False notBefore=-: HTTP 403: x" },
        { "secondary rate limit with Retry-After", Response(403, """{"message":"You have exceeded a secondary rate limit. Please wait a few minutes before you try again."}""", "Retry-After: 60"), false,
            "RateLimited uncertain=False notBefore=2026-10-26T12:01:00.0000000+00:00: HTTP 403: You have exceeded a secondary rate limit. Please wait a few minutes before you try again." },
        { "secondary rate limit by message", Response(403, """{"message":"You have exceeded a secondary rate limit."}"""), false,
            "RateLimited uncertain=False notBefore=-: HTTP 403: You have exceeded a secondary rate limit." },
        { "abuse detection", Response(403, """{"message":"You have triggered an Abuse detection mechanism."}"""), false,
            "RateLimited uncertain=False notBefore=-: HTTP 403: You have triggered an Abuse detection mechanism." },
        { "Retry-After and reset", Response(403, """{"message":"x"}""", "Retry-After: 7200", "X-RateLimit-Remaining: 0", "X-RateLimit-Reset: 1793020800"), false,
            "RateLimited uncertain=False notBefore=2026-10-26T14:00:00.0000000+00:00: HTTP 403: x" },
        { "Retry-After negative", Response(403, """{"message":"x"}""", "Retry-After: -1"), false, "Permanent uncertain=False notBefore=-: HTTP 403: x" },
        { "Retry-After a date", Response(403, """{"message":"x"}""", "Retry-After: Wed, 21 Oct 2026 07:28:00 GMT"), false, "Permanent uncertain=False notBefore=-: HTTP 403: x" },
        { "Retry-After on 200", Response(200, """{"data":{"x":1}}""", "Retry-After: 5"), false, """data {"x":1}""" },
        { "HTTP 429", Response(429, """{"message":"Too many requests"}""", "Retry-After: 30"), false,
            "RateLimited uncertain=False notBefore=2026-10-26T12:00:30.0000000+00:00: HTTP 429: Too many requests" },
        { "HTTP 429 without Retry-After", Response(429, ""), false, "RateLimited uncertain=False notBefore=-: HTTP 429" },
        { "forbidden", Response(403, """{"message":"Resource not accessible by integration"}"""), false, "Permanent uncertain=False notBefore=-: HTTP 403: Resource not accessible by integration" },
        { "HTTP 401", Response(401, """{"message":"Bad credentials"}"""), false, "Transient uncertain=False notBefore=-: HTTP 401: Bad credentials" },
        { "HTTP 400", Response(400, """{"message":"Problems parsing JSON"}"""), false, "Permanent uncertain=False notBefore=-: HTTP 400: Problems parsing JSON" },
        { "HTTP 404", Response(404, """{"message":"Not Found"}"""), false, "Permanent uncertain=False notBefore=-: HTTP 404: Not Found" },
        { "HTTP 410", Response(410, ""), false, "Permanent uncertain=False notBefore=-: HTTP 410" },
        { "HTTP 422", Response(422, """{"message":"Validation Failed"}"""), false, "Permanent uncertain=False notBefore=-: HTTP 422: Validation Failed" },
        { "HTTP 408", Response(408, ""), false, "Transient uncertain=True notBefore=-: HTTP 408" },
        { "HTTP 409", Response(409, """{"message":"Conflict"}"""), false, "Transient uncertain=True notBefore=-: HTTP 409: Conflict" },
        { "HTTP 500", Response(500, """{"message":"Internal Server Error"}"""), false, "Transient uncertain=True notBefore=-: HTTP 500: Internal Server Error" },
        { "HTTP 502 HTML", Response(502, "<html><body>Bad gateway</body></html>"), false, "Transient uncertain=True notBefore=-: HTTP 502" },
        { "HTTP 503", Response(503, """{"message":"Service Unavailable"}"""), false, "Transient uncertain=True notBefore=-: HTTP 503: Service Unavailable" },
        { "HTTP 304", Response(304, ""), false, "Transient uncertain=True notBefore=-: HTTP 304" },
        { "unrecognized output", "gh: something went wrong", false, "Transient uncertain=True notBefore=-: unrecognized gh response" },
        { "no blank line", "HTTP/2.0 200 OK\r\nContent-Type: application/json\r\n", false, "Transient uncertain=True notBefore=-: unrecognized gh response" },
        { "LF headers", "HTTP/1.1 200 OK\nContent-Type: application/json\n\n{\"data\":{\"x\":1}}", false, """data {"x":1}""" },
        { "body message after error without one", Response(200, """{"message":"outer","errors":[{"type":"FORBIDDEN"}]}"""), false, "Permanent uncertain=False notBefore=-: HTTP 200 FORBIDDEN: outer" },
        { "error message not text", Response(200, """{"message":"outer","errors":[{"type":"FORBIDDEN","message":5}]}"""), false, "Permanent uncertain=False notBefore=-: HTTP 200 FORBIDDEN" },
        { "errors not a list", Response(200, """{"data":{"x":1},"errors":{"type":"FORBIDDEN"}}"""), false, """data {"x":1}""" },
        { "error type not text", Response(200, """{"errors":[{"type":5,"message":"a"}]}"""), false, "Transient uncertain=True notBefore=-: HTTP 200: a" },
        { "data null", Response(200, """{"data":null}"""), false, "Transient uncertain=True notBefore=-: HTTP 200: no data" },
        { "body a list", Response(200, "[]"), false, "InvalidOperationException" },
        { "body a number", Response(200, "5"), false, "InvalidOperationException" },
        { "error not an object", Response(200, """{"errors":[5]}"""), false, "InvalidOperationException" },
        { "null error", Response(200, """{"errors":[null]}"""), false, "Transient uncertain=True notBefore=-: HTTP 200" },
        { "error 403 body a list", Response(403, "[]"), false, "InvalidOperationException" },

        // A Discussion lookup: only the typed NOT_FOUND for that field inside the resolved repository confirms deletion.
        { "lookup found", Response(200, """{"data":{"repository":{"discussion":{"id":"D1"}}}}"""), true, """discussion {"id":"D1"}""" },
        { "lookup typed NOT_FOUND", Response(200, """{"data":{"repository":{"discussion":null}},"errors":[{"type":"NOT_FOUND","path":["repository","discussion"],"message":"Could not resolve to a Discussion with the number of 1."}]}"""), true, "absent" },
        { "lookup repository NOT_FOUND", Response(200, """{"data":{"repository":null},"errors":[{"type":"NOT_FOUND","path":["repository"],"message":"Could not resolve to a Repository with the name 'owner/project'."}]}"""), true,
            "Permanent uncertain=False notBefore=-: HTTP 200 NOT_FOUND: Could not resolve to a Repository with the name 'owner/project'." },
        { "lookup FORBIDDEN", Response(200, """{"data":{"repository":{"discussion":null}},"errors":[{"type":"FORBIDDEN","path":["repository","discussion"],"message":"Resource not accessible by integration"}]}"""), true,
            "Permanent uncertain=False notBefore=-: HTTP 200 FORBIDDEN: Resource not accessible by integration" },
        { "lookup RATE_LIMITED", Response(200, """{"errors":[{"type":"RATE_LIMITED","message":"API rate limit exceeded for installation."}]}"""), true,
            "RateLimited uncertain=False notBefore=-: HTTP 200 RATE_LIMITED: API rate limit exceeded for installation." },
        { "lookup untyped missing message", Response(200, """{"data":{"repository":{"discussion":null}},"errors":[{"path":["repository","discussion"],"message":"Could not resolve to a Discussion with the number of 1."}]}"""), true,
            "Transient uncertain=True notBefore=-: HTTP 200: " + Missing },
        { "lookup null without error", Response(200, """{"data":{"repository":{"discussion":null}}}"""), true, "Transient uncertain=False notBefore=-: incomplete Discussion response" },
        { "lookup HTTP 502", Response(502, """{"message":"Server Error"}"""), true, "Transient uncertain=True notBefore=-: HTTP 502: Server Error" },
        // bcfd719 threw InvalidOperationException reading the field through data or a repository that is no object.
        { "lookup data not an object", Response(200, """{"data":5}"""), true, "Transient uncertain=False notBefore=-: incomplete Discussion response" },
        { "lookup data a list", Response(200, """{"data":[]}"""), true, "Transient uncertain=False notBefore=-: incomplete Discussion response" },
        { "lookup data not an object typed NOT_FOUND", Response(200, """{"data":5,"errors":[{"type":"NOT_FOUND","path":["repository","discussion"],"message":"a"}]}"""), true,
            "Permanent uncertain=False notBefore=-: HTTP 200 NOT_FOUND: a" },
        { "lookup data a list typed NOT_FOUND", Response(200, """{"data":[],"errors":[{"type":"NOT_FOUND","path":["repository","discussion"],"message":"a"}]}"""), true,
            "Permanent uncertain=False notBefore=-: HTTP 200 NOT_FOUND: a" },
        { "lookup repository a list", Response(200, """{"data":{"repository":[]}}"""), true, "Transient uncertain=False notBefore=-: incomplete Discussion response" },
        { "lookup repository a list typed NOT_FOUND", Response(200, """{"data":{"repository":[]},"errors":[{"type":"NOT_FOUND","path":["repository","discussion"],"message":"a"}]}"""), true,
            "Permanent uncertain=False notBefore=-: HTTP 200 NOT_FOUND: a" },
        { "lookup NOT_FOUND with a second error", Response(200, """{"data":{"repository":{"discussion":null}},"errors":[{"type":"NOT_FOUND","path":["repository","discussion"],"message":"a"},{"type":"NOT_FOUND","path":["repository","discussion"],"message":"b"}]}"""), true,
            "Permanent uncertain=False notBefore=-: HTTP 200 NOT_FOUND: a" },
        { "lookup NOT_FOUND on 404", Response(404, """{"data":{"repository":{"discussion":null}},"errors":[{"type":"NOT_FOUND","path":["repository","discussion"],"message":"a"}]}"""), true,
            "Permanent uncertain=False notBefore=-: HTTP 404 NOT_FOUND: a" },
        { "lookup NOT_FOUND other path", Response(200, """{"data":{"repository":{"discussion":null}},"errors":[{"type":"NOT_FOUND","path":["repository","discussion","author"],"message":"a"}]}"""), true,
            "Permanent uncertain=False notBefore=-: HTTP 200 NOT_FOUND: a" },
        { "lookup NOT_FOUND numeric path", Response(200, """{"data":{"repository":{"discussion":null}},"errors":[{"type":"NOT_FOUND","path":["repository",1],"message":"a"}]}"""), true,
            "Permanent uncertain=False notBefore=-: HTTP 200 NOT_FOUND: a" },
        { "lookup NOT_FOUND field missing", Response(200, """{"data":{"repository":{}},"errors":[{"type":"NOT_FOUND","path":["repository","discussion"],"message":"a"}]}"""), true,
            "Permanent uncertain=False notBefore=-: HTTP 200 NOT_FOUND: a" },
        { "lookup NOT_FOUND field present", Response(200, """{"data":{"repository":{"discussion":{"id":"D1"}}},"errors":[{"type":"NOT_FOUND","path":["repository","discussion"],"message":"a"}]}"""), true,
            "Permanent uncertain=False notBefore=-: HTTP 200 NOT_FOUND: a" },
        { "lookup NOT_FOUND repository not an object", Response(200, """{"data":{"repository":5},"errors":[{"type":"NOT_FOUND","path":["repository","discussion"],"message":"a"}]}"""), true,
            "Permanent uncertain=False notBefore=-: HTTP 200 NOT_FOUND: a" },
        { "lookup NOT_FOUND no path", Response(200, """{"data":{"repository":{"discussion":null}},"errors":[{"type":"NOT_FOUND","message":"a"}]}"""), true,
            "Permanent uncertain=False notBefore=-: HTTP 200 NOT_FOUND: a" },
        { "lookup NOT_FOUND not JSON", Response(200, "Not Found"), true, "Transient uncertain=True notBefore=-: HTTP 200: no data" },
        { "lookup repository null without error", Response(200, """{"data":{"repository":null}}"""), true, "Transient uncertain=False notBefore=-: incomplete Discussion response" },
        // bcfd719 threw InvalidOperationException reading the field through a repository that is no object.
        { "lookup repository not an object", Response(200, """{"data":{"repository":5}}"""), true, "Transient uncertain=False notBefore=-: incomplete Discussion response" },
        { "lookup body a list", Response(200, "[]"), true, "InvalidOperationException" },
        { "lookup unrecognized", "gh: x", true, "Transient uncertain=True notBefore=-: unrecognized gh response" },
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public void A_gh_response_reads_as_bcfd719_read_it(string form, string response, bool lookup, string expected)
    {
        Assert.NotEmpty(form);
        Assert.Equal(expected, Outcome(response, lookup));
    }

    // A lookup reads as GitHubDiscussions.Find reads its response; any other request as GitHub.Send does.
    static string Outcome(string response, bool lookup)
    {
        try
        {
            if (!lookup) return "data " + GitHub.Classify(response).ToJsonString();
            return GitHubDiscussions.Discussion(response) is { } discussion ? "discussion " + discussion.ToJsonString() : "absent";
        }
        catch (GitHubRequestException error) { return $"{error.Kind} uncertain={error.Uncertain} notBefore={error.NotBefore(Now)?.ToString("O") ?? "-"}: {error.Message}"; }
        catch (Exception error) { return error.GetType().Name; }
    }
}
