using Xunit;

namespace RepoAgentController.Tests;

public sealed class ControllerCoreTests
{
    // ADR 0010: the core holds decisions only. Processes, HTTP, HTML parsing, the CLI and the adapters stay in RepoAgentController.
    [Fact]
    public void Core_references_no_process_HTTP_HTML_CLI_or_adapter_assembly()
    {
        var references = typeof(IControllerLog).Assembly.GetReferencedAssemblies().Select(reference => reference.Name);

        Assert.DoesNotContain(references, name => name is "System.Diagnostics.Process" or "System.Net.Http" or "AngleSharp"
            or "System.CommandLine" or "RepoAgentController");
    }
}
