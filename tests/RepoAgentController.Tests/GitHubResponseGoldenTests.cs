using Xunit;

namespace RepoAgentController.Tests;

// How GitHub.Read translates gh --include output into the facts of a GitHubResponse, one row per fact it extracts and per
// damaged JSON shape whose reading changed. What the core decides from those facts is GitHubResponsesTests'.
public sealed class GitHubResponseGoldenTests
{
    static string Response(int status, string body, params string[] headers) =>
        $"HTTP/2.0 {status} {(status == 200 ? "OK" : "Status")}\r\n" + string.Concat(headers.Select(header => header + "\r\n")) + "Content-Type: application/json\r\n\r\n" + body;

    const string None = "data=False errors=[] message=\"\" remaining=- reset=- retryAfter=- field=Missing value=-";

    public static TheoryData<string, string, bool, string> Forms() => new()
    {
        // The status line, headers and body.
        { "data", Response(200, """{"data":{"node":{"id":"D1"}}}"""), false,
            """200 data=True errors=[] message="" remaining=- reset=- retryAfter=- field=Missing value={"node":{"id":"D1"}}""" },
        { "LF headers", "HTTP/1.1 201 Created\nContent-Type: application/json\n\n{\"data\":{\"x\":1}}", false,
            """201 data=True errors=[] message="" remaining=- reset=- retryAfter=- field=Missing value={"x":1}""" },
        { "unrecognized output", "gh: something went wrong", false, "unrecognized" },
        { "no blank line", "HTTP/2.0 200 OK\r\nContent-Type: application/json\r\n", false, "unrecognized" },
        { "body not JSON", Response(502, "<html><body>Bad gateway</body></html>"), false, "502 " + None },
        { "no data", Response(200, "{}"), false, "200 " + None },
        { "data null", Response(200, """{"data":null}"""), false, "200 " + None },

        // Rate-limit and retry headers, in any case; a missing or unusable value is null.
        { "rate-limit headers", Response(403, "", "X-RateLimit-Remaining: 0", "X-RateLimit-Reset: 1793020800", "Retry-After: 60"), false,
            "403 data=False errors=[] message=\"\" remaining=0 reset=2026-10-26T13:20:00.0000000+00:00 retryAfter=00:01:00 field=Missing value=-" },
        { "header names lower case", Response(403, "", "x-ratelimit-remaining: 4999", "x-ratelimit-reset: 1793020800", "retry-after: 0"), false,
            "403 data=False errors=[] message=\"\" remaining=4999 reset=2026-10-26T13:20:00.0000000+00:00 retryAfter=00:00:00 field=Missing value=-" },
        // bcfd719 compared the header's text with "0", so 00 was not exhausted.
        { "remaining 00", Response(403, "", "X-RateLimit-Remaining: 00"), false, "403 data=False errors=[] message=\"\" remaining=0 reset=- retryAfter=- field=Missing value=-" },
        { "remaining with a sign", Response(403, "", "X-RateLimit-Remaining: +0"), false, "403 " + None },
        { "remaining not a number", Response(403, "", "X-RateLimit-Remaining: none"), false, "403 " + None },
        { "reset not a number", Response(403, "", "X-RateLimit-Reset: soon"), false, "403 " + None },
        // bcfd719 threw ArgumentOutOfRangeException converting an exhausted limit's reset to a time.
        { "reset out of range", Response(403, "", "X-RateLimit-Reset: 99999999999999"), false, "403 " + None },
        { "reset before the earliest time", Response(403, "", "X-RateLimit-Reset: -99999999999999"), false, "403 " + None },
        { "Retry-After negative", Response(403, "", "Retry-After: -1"), false, "403 " + None },
        { "Retry-After a date", Response(403, "", "Retry-After: Wed, 21 Oct 2026 07:28:00 GMT"), false, "403 " + None },
        { "Retry-After past int", Response(403, "", "Retry-After: 99999999999"), false, "403 " + None },

        // GraphQL errors, in order: each one's type and path, and the first one's message, else the body's.
        { "typed errors", Response(200, """{"data":{"addReaction":null},"errors":[{"type":"FORBIDDEN","path":["addReaction"],"message":"a"},{"type":"UNPROCESSABLE","message":"b"}]}"""), false,
            """200 data=True errors=[FORBIDDEN@addReaction UNPROCESSABLE@] message="a" remaining=- reset=- retryAfter=- field=Missing value={"addReaction":null}""" },
        { "untyped error with a numeric path", Response(200, """{"errors":[{"path":["repository",1],"message":"a"}]}"""), false,
            """200 data=False errors=[@repository.#] message="a" remaining=- reset=- retryAfter=- field=Missing value=-""" },
        { "error type not text", Response(200, """{"errors":[{"type":5,"message":"a"}]}"""), false,
            """200 data=False errors=[@] message="a" remaining=- reset=- retryAfter=- field=Missing value=-""" },
        { "body message after an error without one", Response(403, """{"message":"outer","errors":[{"type":"FORBIDDEN"}]}"""), false,
            """403 data=False errors=[FORBIDDEN@] message="outer" remaining=- reset=- retryAfter=- field=Missing value=-""" },
        { "error message not text", Response(200, """{"message":"outer","errors":[{"type":"FORBIDDEN","message":5}]}"""), false,
            """200 data=False errors=[FORBIDDEN@] message="" remaining=- reset=- retryAfter=- field=Missing value=-""" },
        { "errors not a list", Response(200, """{"data":{"x":1},"errors":{"type":"FORBIDDEN"}}"""), false,
            """200 data=True errors=[] message="" remaining=- reset=- retryAfter=- field=Missing value={"x":1}""" },
        { "null error", Response(200, """{"errors":[null]}"""), false, """200 data=False errors=[@] message="" remaining=- reset=- retryAfter=- field=Missing value=-""" },
        // bcfd719 threw InvalidOperationException reading fields from a body or error that is no object.
        { "error not an object", Response(200, """{"message":"outer","errors":[5]}"""), false,
            """200 data=False errors=[@] message="outer" remaining=- reset=- retryAfter=- field=Missing value=-""" },
        { "body a list", Response(403, """[{"message":"a"}]"""), false, "403 " + None },
        { "body a number", Response(200, "5"), false, "200 " + None },

        // A Discussion lookup: what the data holds at repository.discussion, and the node found there.
        { "lookup found", Response(200, """{"data":{"repository":{"discussion":{"id":"D1"}}}}"""), true,
            """200 data=True errors=[] message="" remaining=- reset=- retryAfter=- field=Value value={"id":"D1"}""" },
        { "lookup typed NOT_FOUND", Response(200, """{"data":{"repository":{"discussion":null}},"errors":[{"type":"NOT_FOUND","path":["repository","discussion"],"message":"a"}]}"""), true,
            """200 data=True errors=[NOT_FOUND@repository.discussion] message="a" remaining=- reset=- retryAfter=- field=Null value=-""" },
        { "lookup field missing", Response(200, """{"data":{"repository":{}}}"""), true, """200 data=True errors=[] message="" remaining=- reset=- retryAfter=- field=Missing value=-""" },
        { "lookup repository null", Response(200, """{"data":{"repository":null}}"""), true, """200 data=True errors=[] message="" remaining=- reset=- retryAfter=- field=Missing value=-""" },
        { "lookup data null", Response(200, """{"data":null}"""), true, "200 " + None },
        // bcfd719 threw InvalidOperationException reading the field through data or a repository that is no object.
        { "lookup repository not an object", Response(200, """{"data":{"repository":5}}"""), true, """200 data=True errors=[] message="" remaining=- reset=- retryAfter=- field=Missing value=-""" },
        { "lookup repository a list", Response(200, """{"data":{"repository":[{"discussion":null}]}}"""), true,
            """200 data=True errors=[] message="" remaining=- reset=- retryAfter=- field=Missing value=-""" },
        { "lookup data a list", Response(200, """{"data":[]}"""), true, """200 data=True errors=[] message="" remaining=- reset=- retryAfter=- field=Missing value=-""" },
        { "lookup data not an object", Response(200, """{"data":5}"""), true, """200 data=True errors=[] message="" remaining=- reset=- retryAfter=- field=Missing value=-""" },
        { "lookup body a list", Response(200, """[{"data":{"repository":{"discussion":null}}}]"""), true, "200 " + None },
    };

    [Theory]
    [MemberData(nameof(Forms))]
    public void A_gh_response_reads_as_these_facts(string form, string response, bool lookup, string expected)
    {
        Assert.NotEmpty(form);
        Assert.Equal(expected, Facts(response, lookup));
    }

    [Fact]
    public void A_request_answers_the_responses_data_or_throws_what_the_core_decides()
    {
        Assert.Equal("""{"data":{"id":"D1"}}""", GitHub.Classify(Response(200, """{"data":{"data":{"id":"D1"}}}""")).ToJsonString());
        Assert.Throws<GitHubRequestException>(() => GitHub.Classify(Response(502, "<html></html>")));
    }

    static string Facts(string response, bool lookup)
    {
        var (read, value) = lookup ? GitHub.Read(response, GitHubDiscussions.Lookup) : GitHub.Read(response);
        if (read is null) return "unrecognized";
        var errors = string.Join(" ", read.Errors.Select(error => error.Type + "@" + string.Join(".", error.Path.Select(segment => segment ?? "#"))));
        return $"{read.Status} data={read.HasData} errors=[{errors}] message=\"{read.Message}\" remaining={read.RateLimitRemaining?.ToString() ?? "-"} " +
            $"reset={read.RateLimitReset?.ToString("O") ?? "-"} retryAfter={read.RetryAfter?.ToString() ?? "-"} field={read.Field} value={value?.ToJsonString() ?? "-"}";
    }
}
