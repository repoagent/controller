using Xunit;

namespace RepoAgentController.Tests;

// ModelSelection.Resolve in-process, with the model catalogue that ModelSelectionTests' peer copies from pinned Codex 0.163.0 model/list.
public sealed class ModelSelectionResolveTests
{
    static readonly AgentModel[] Catalogue =
    [
        new("gpt-6.1-sol", true, "low", ["low", "medium", "high", "xhigh", "max", "ultra"]),
        new("gpt-6-luna", false, "medium", ["low", "medium", "high", "xhigh", "max"]),
        new("gpt-5.5", false, "medium", ["low", "medium", "high", "xhigh"]),
    ];

    [Theory]
    [InlineData(null, null, null, null, "gpt-6.1-sol", "low")]
    [InlineData(null, null, "gpt-5.5", null, "gpt-5.5", "medium")]
    [InlineData(null, null, "gpt-5.5", "high", "gpt-5.5", "high")]
    [InlineData("luna6", null, "gpt-5.5", "high", "gpt-6-luna", "high")]
    [InlineData(null, "xhigh", "gpt-5.5", null, "gpt-5.5", "xhigh")]
    public void An_unset_repository_default_falls_back_to_the_agent_configuration_then_the_model_catalogue(
        string? model, string? reasoning, string? agentModel, string? agentReasoning, string expectedModel, string expectedEffort)
    {
        var choice = ModelSelection.Resolve([], Setting(model), Setting(reasoning), agentModel, agentReasoning, Catalogue);

        Assert.Equal(new ModelChoice(expectedModel, expectedEffort, "default"), choice);
    }

    // A successful settings read: the normalized variable value, or GitHub's confirmed absence.
    static SettingValue Setting(string? value) => value is null ? new(SettingState.Absent, null) : new(SettingState.Configured, value);
}
