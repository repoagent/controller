using System.Text.Json.Nodes;
using Xunit;

namespace RepoAgentController.Tests;

// Operator status in-process (BridgeWorld): the times status takes itself. Times of logged events are the log's.
public sealed class ControllerStatusTests
{
    [Fact]
    public async Task Status_times_the_lifetime_and_each_report_on_the_controller_clock()
    {
        await using var world = new BridgeWorld();
        static string At(int seconds) => BridgeWorld.Start.AddSeconds(seconds).ToString("O");
        static (string?, string?, string?, string?) Times(JsonObject status) => (status["controller"]!["startedAt"]?.GetValue<string>(),
            status["controller"]!["stoppedAt"]?.GetValue<string>(), status["controller"]!["updatedAt"]?.GetValue<string>(), status["generatedAt"]!.GetValue<string>());

        await world.Begin();
        world.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal((At(0), null, At(0), At(30)), Times(world.Status()));

        world.Clock.Advance(TimeSpan.FromSeconds(30));
        await world.End();
        world.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal((At(0), At(60), At(60), At(90)), Times(world.Status())); // As the stopped controller recorded it.
    }
}
