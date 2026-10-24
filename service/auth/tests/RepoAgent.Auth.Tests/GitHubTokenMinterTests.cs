using Xunit;

namespace RepoAgent.Auth.Tests;

// The broker core's rules over what GitHub answers to GitHub token mints and revocations. Only an answer that carries
// a usable token makes a token known, and a known token is in scope only when GitHub reports exactly the repository,
// the runtime authority and a lifetime the broker asked for. A token out of scope is still known, so it can be revoked.
public sealed class GitHubTokenMinterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly RepositoryIdentity Project = InMemoryGitHub.Project;
    private const string Minted = "minted-token";
    private readonly IssuanceClock clock = new();
    private readonly InMemoryGitHub github;

    public GitHubTokenMinterTests() => github = new(clock);

    private GitHubTokenMinter Minter => new(github, clock);

    // Minting

    [Theory]
    [InlineData("polling")]
    [InlineData("webhook-clone-settings")]
    [InlineData("actions-variable-verification")]
    public async Task Mint_asks_GitHub_once_for_the_installation_repository_and_runtime_authority(string profile)
    {
        var known = await MintAsync(profile);

        var mint = Assert.Single(github.MintRequests);
        Assert.Equal((789L, Project), (mint.InstallationId, mint.Repository));
        Assert.Equal(RuntimeAuthority.For(profile), mint.Authority);
        Assert.True(known.ScopeValid);
        Assert.Equal(new GitHubToken(mint.Verifier ? "verifier-token" : "runtime-token-1", clock.GetUtcNow().AddHours(1)), known.Token);
        Assert.Equal(1, github.Authorizations);
    }

    [Theory]
    [InlineData("not-found")]
    [InlineData("refused")]
    [InlineData("malformed")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("non-ascii")]
    public async Task Answer_without_a_usable_token_makes_no_token_known(string answer)
    {
        github.MintAnswer = (_, token) => answer switch
        {
            "not-found" => new GitHubRead<MintedGitHubToken>.NotFound(),
            "refused" => new GitHubRead<MintedGitHubToken>.Refused(),
            "malformed" => new GitHubRead<MintedGitHubToken>.Malformed(),
            "empty" => GitHubRead.Found(token with { Value = "" }),
            "whitespace" => GitHubRead.Found(token with { Value = "runtime token" }),
            _ => GitHubRead.Found(token with { Value = "runtime-tökén" })
        };

        await Assert.ThrowsAsync<IssuanceDenied>(() => MintAsync());
        Assert.Equal(1, github.Mints);
    }

    [Theory]
    [InlineData("exact", true)]
    [InlineData("name-in-other-case", true)]
    [InlineData("every-repository", false)]
    [InlineData("selection-unreadable", false)]
    [InlineData("no-repository", false)]
    [InlineData("two-repositories", false)]
    [InlineData("other-id", false)]
    [InlineData("other-name", false)]
    [InlineData("repositories-unreadable", false)]
    public async Task Token_is_in_scope_only_for_exactly_the_repository_asked_for(string answer, bool inScope)
    {
        var other = new RepositoryIdentity(654321, "other/project");
        Answer(token => answer switch
        {
            "exact" => token,
            "name-in-other-case" => token with { Repositories = [Project with { FullName = "Owner/Project" }] },
            "every-repository" => token with { OnlyListedRepositories = false },
            "selection-unreadable" => token with { OnlyListedRepositories = null },
            "no-repository" => token with { Repositories = [] },
            "two-repositories" => token with { Repositories = [Project, other] },
            "other-id" => token with { Repositories = [Project with { Id = other.Id }] },
            "other-name" => token with { Repositories = [Project with { FullName = other.FullName }] },
            _ => token with { Repositories = null }
        });

        await AssertScopeAsync(inScope);
    }

    [Theory]
    [InlineData("polling", "exact", true)]
    [InlineData("polling", "metadata-read", true)]
    [InlineData("polling", "metadata-write", false)]
    [InlineData("polling", "missing", false)]
    [InlineData("polling", "lower", false)]
    [InlineData("agent-variable-verification", "higher", false)]
    [InlineData("agent-variable-verification", "extra", false)]
    [InlineData("polling", "actions-variables", false)]
    [InlineData("polling-settings", "actions-variables", true)]
    [InlineData("polling", "level-unreadable", false)]
    [InlineData("polling", "permissions-unreadable", false)]
    public async Task Token_carries_exactly_the_runtime_authority_asked_for(string profile, string answer, bool inScope)
    {
        var asked = RuntimeAuthority.For(profile).Permissions;
        IReadOnlyDictionary<string, string?>? Reported() => answer switch
        {
            "exact" => With(),
            "metadata-read" => With(("metadata", "read")),
            "metadata-write" => With(("metadata", "write")),
            "missing" => new Dictionary<string, string?>(),
            "lower" => With(("discussions", "read")),
            "higher" => With(("agent_variables", "write")),
            "extra" => With(("actions", "read")),
            "actions-variables" => With(("actions_variables", "read")),
            "level-unreadable" => With(("discussions", null)),
            _ => null
        };
        Dictionary<string, string?> With(params (string Name, string? Level)[] changes)
        {
            var permissions = asked.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.Ordinal);
            foreach (var (name, level) in changes) permissions[name] = level;
            return permissions;
        }
        Answer(token => token with { Permissions = Reported() });

        await AssertScopeAsync(inScope, profile);
    }

    [Theory]
    [InlineData(-60, false)]
    [InlineData(300, false)]
    [InlineData(301, true)]
    [InlineData(3900, true)]
    [InlineData(3901, false)]
    public async Task Token_must_outlive_the_renewal_margin_and_claim_at_most_65_minutes(int seconds, bool inScope)
    {
        var expiry = clock.GetUtcNow().AddSeconds(seconds);
        Answer(token => token with { ExpiresAt = expiry });

        var known = await AssertScopeAsync(inScope);

        Assert.Equal(expiry, known.Token.ExpiresAt);
    }

    [Fact]
    public async Task Unreadable_expiry_is_out_of_scope_and_never_treated_as_passed()
    {
        Answer(token => token with { ExpiresAt = null });

        var known = await AssertScopeAsync(false);

        Assert.Equal(DateTimeOffset.MaxValue, known.Token.ExpiresAt);
    }

    // Revocation

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Revocation_returns_only_when_GitHub_confirms_it(bool confirmed)
    {
        var token = new GitHubToken("runtime-token-1", clock.GetUtcNow().AddHours(1));
        github.RevocationConfirmed = confirmed;

        var revocation = Minter.RevokeAsync(token, Token);

        if (confirmed) await revocation;
        else await Assert.ThrowsAsync<IssuanceDenied>(() => revocation);
        Assert.Equal<GitHubToken>([token], github.Revoked);
    }

    // GitHub answers the default token for exactly what was asked, changed as the test says.
    private void Answer(Func<MintedGitHubToken, MintedGitHubToken> change) =>
        github.MintAnswer = (_, token) => GitHubRead.Found(change(token with { Value = Minted }));

    // An out-of-scope token is still returned, with the value GitHub answered, so the caller can revoke it.
    private async Task<KnownGitHubToken> AssertScopeAsync(bool inScope, string profile = "polling")
    {
        var known = await MintAsync(profile);
        Assert.Equal(inScope, known.ScopeValid);
        Assert.Equal(Minted, known.Token.Value);
        return known;
    }

    private async Task<KnownGitHubToken> MintAsync(string profile = "polling")
    {
        var now = clock.GetUtcNow();
        var app = await github.AuthorizeAsync(AppAuthorizationWindow.Reserved(now, now.AddSeconds(-60), now.AddSeconds(120)), Token);
        return await Minter.MintAsync(app, 789, Project, RuntimeAuthority.For(profile), Token);
    }
}
