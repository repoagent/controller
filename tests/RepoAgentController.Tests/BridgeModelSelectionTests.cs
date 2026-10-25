using Xunit;

namespace RepoAgentController.Tests;

// The model and reasoning each turn request states, from the Discussion's labels and the repository defaults, in-process
// (BridgeWorld). The agent's catalogue is pinned Codex 0.163.0's model/list; its own configuration sets none.
// ModelSelectionResolveTests owns the resolution order; here the bridge observes labels and holds unusable selections.
public sealed class BridgeModelSelectionTests
{
    static BridgeWorld Enrolled()
    {
        var world = new BridgeWorld
        {
            Native = runtime => runtime.Catalogue =
            [
                new("gpt-6.1-sol", true, "low", ["low", "medium", "high", "xhigh", "max", "ultra"]),
                new("gpt-6-luna", false, "medium", ["low", "medium", "high", "xhigh", "max"]),
                new("gpt-5.5", false, "medium", ["low", "medium", "high", "xhigh"])
            ]
        };
        world.GitHub.Open(1, "Please review the fixture.");
        return world;
    }

    static (string, string)[] Selected(BridgeWorld world) => [.. world.Turns.Select(turn => (turn.Model.Model, turn.Model.Effort))];

    [Fact]
    public async Task A_model_label_selects_the_model_and_reasoning_of_the_turn()
    {
        await using var world = Enrolled();
        world.GitHub.Labels(1, "question", "repoagent:luna6:xhigh");

        Assert.True(await world.RunOnce(1));

        Assert.Equal([("gpt-6-luna", "xhigh")], Selected(world));
        Assert.Contains("session=S1 model=gpt-6-luna effort=xhigh selection=label turn=T1", world.Logged("native-accepted"));
    }

    [Fact]
    public async Task Without_a_model_label_the_turn_uses_the_repository_defaults()
    {
        await using var world = Enrolled();
        world.GitHub.Labels(1, "question");
        world.Variables.Set(ModelSelection.ModelSetting, " Luna6 ");
        world.Variables.Set(ModelSelection.ReasoningSetting, "HIGH");

        Assert.True(await world.RunOnce(1));

        Assert.Contains("name=REPOAGENT_MODEL state=configured value=luna6", world.Logged("repository-setting"));
        Assert.Equal([("gpt-6-luna", "high")], Selected(world));
    }

    // Pinned Codex keeps a turn's model and effort for later turns of the session, so each turn states them, defaults too.
    [Fact]
    public async Task A_label_change_applies_to_the_next_turn_without_starting_one_and_removal_restores_the_defaults()
    {
        await using var world = Enrolled();
        world.GitHub.Labels(1, "repoagent:luna6:xhigh");
        Assert.True(await world.RunOnce(1));

        world.GitHub.Labels(1, "Repoagent:GPT-5.5:High");
        Assert.True(await world.RunOnce(1));
        Assert.Single(world.Turns); // A label-only change does not invoke the agent.

        world.GitHub.Comment(1, "C1", "Next question.");
        Assert.True(await world.RunOnce(1));
        world.GitHub.Labels(1);
        Assert.True(await world.RunOnce(1));
        world.GitHub.Comment(1, "C2", "Another question.");
        Assert.True(await world.RunOnce(1));

        Assert.Equal([("gpt-6-luna", "xhigh"), ("gpt-5.5", "high"), ("gpt-6.1-sol", "low")], Selected(world));
        Assert.Equal(["label", "label", "default"], world.Turns.Select(turn => turn.Model.Source));
    }

    [Theory]
    [InlineData("repoagent:luna6:xhigh,repoagent:sol6.1:low", null, null,
        "conflicting model labels repoagent:luna6:xhigh, repoagent:sol6.1:low; keep one repoagent:<model>:<reasoning> label")]
    [InlineData("repoagent:luna6", null, null, "label repoagent:luna6 must be repoagent:<model>:<reasoning>, such as repoagent:sol6.1:xhigh")]
    [InlineData("repoagent:sun9:high", null, null,
        "model sun9 from label repoagent:sun9:high is not available to this Codex account; available: sol6.1 (gpt-6.1-sol), luna6 (gpt-6-luna), gpt-5.5")]
    [InlineData("repoagent:luna6:ultra", null, null,
        "reasoning ultra from label repoagent:luna6:ultra is not supported by gpt-6-luna; supported: low, medium, high, xhigh, max")]
    [InlineData("", "gpt-5.5", "max", "reasoning max from REPOAGENT_REASONING is not supported by gpt-5.5; supported: low, medium, high, xhigh")]
    [InlineData("", "gpt 6.1", null, "REPOAGENT_MODEL is invalid (must be a name of at most 64 letters, digits, '.', '_' or '-') and no earlier value is known; correct or clear the variable")]
    [InlineData("", null, "very high", "REPOAGENT_REASONING is invalid (must be a name of at most 64 letters, digits, '.', '_' or '-') and no earlier value is known; correct or clear the variable")]
    public async Task An_unusable_selection_holds_the_input_with_an_actionable_error_until_it_is_corrected(string labels, string? model, string? reasoning, string error)
    {
        await using var world = Enrolled();
        world.GitHub.Labels(1, labels.Split(',', StringSplitOptions.RemoveEmptyEntries));
        world.Variables.Set(ModelSelection.ModelSetting, model);
        world.Variables.Set(ModelSelection.ReasoningSetting, reasoning);

        Assert.True(await world.RunOnce(1));

        Assert.Empty(world.Turns);
        Assert.Equal(0, world.GitHub.Writes("D1"));
        Assert.Contains(BridgeWorld.Url(1) + ": " + error, world.Logged("selection-error"));
        var status = world.Status();
        Assert.Contains(error, Assert.Single(status["failures"]!.AsArray(), item => item!["kind"]!.GetValue<string>() == "selection-error")!["message"]!.GetValue<string>());
        Assert.Equal(1, status["work"]!["waiting"]!.GetValue<int>());

        world.GitHub.Labels(1);
        world.Variables.Set(ModelSelection.ModelSetting, null);
        world.Variables.Set(ModelSelection.ReasoningSetting, null);
        Assert.True(await world.RunOnce(1));

        Assert.Equal([("gpt-6.1-sol", "low")], Selected(world));
        Assert.Equal(1, world.GitHub.Writes("D1"));
    }

    // Whether a discussions-only runtime token may read Discussion labels is unverified against live GitHub.
    [Fact]
    public async Task A_failed_label_read_is_reported_and_keeps_the_last_observed_labels()
    {
        await using var world = Enrolled();
        world.GitHub.Labels(1, "repoagent:luna6:xhigh");
        Assert.True(await world.RunOnce(1));

        world.GitHub.Labels(1, "repoagent:gpt-5.5:low");
        world.GitHub.FailReads("Labels", new IOException("FORBIDDEN: Resource not accessible by integration"));
        world.GitHub.Comment(1, "C1", "Next question.");
        Assert.True(await world.RunOnce(1));

        Assert.Equal([("gpt-6-luna", "xhigh"), ("gpt-6-luna", "xhigh")], Selected(world));
        Assert.Contains(world.Logged("label-read-error"), line => line.StartsWith(BridgeWorld.Url(1) + ": ", StringComparison.Ordinal) &&
            line.Contains("Resource not accessible by integration"));
        Assert.Contains(world.Status()["failures"]!.AsArray(), item => item!["kind"]!.GetValue<string>() == "label-read-error");
    }
}
