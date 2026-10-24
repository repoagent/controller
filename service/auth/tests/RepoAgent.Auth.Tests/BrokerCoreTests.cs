using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class BrokerCoreTests
{
    // ADR 0010: the core holds decisions only. Azure, HTTP and ASP.NET Core stay in the adapters.
    [Fact]
    public void Core_references_no_Azure_HTTP_or_ASP_NET_Core_assembly()
    {
        var references = typeof(ISecretProtection).Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? "");

        Assert.DoesNotContain(references, name => name == "Azure" || name.StartsWith("Azure.", StringComparison.Ordinal)
            || name == "System.Net.Http" || name.StartsWith("Microsoft.AspNetCore.", StringComparison.Ordinal));
    }
}
