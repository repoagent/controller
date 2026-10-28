using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

[Collection("Managed checkout environment")]
public sealed class ControllerShutdownTests
{
    [Fact]
    public async Task Sigterm_keeps_accepted_native_work_and_credentials_alive_until_correlated_completion()
    {
        if (!OperatingSystem.IsLinux()) return; // Actual shipping OS signal, Unix credential socket and native relay.
        await using var controller = new ControllerHarness.Controller(1);
        await controller.StartChild();
        var turn = await controller.Turn(1); // Native acknowledgement and durable acceptance have both completed.
        Assert.Null(controller.DiscussionState(1)["pending"]);
        Assert.NotNull(controller.DiscussionState(1)["running"]);
        Assert.Equal(0, await controller.InvokeWrapper());

        Console.WriteLine("SHUTDOWN_SIGNAL accepted=S1/T1 running=true pending=false credential-wrapper-exit=0 completion-withheld=true");
        controller.TerminateChild();
        // This is the explicit CLI termination-budget observation; completion remains withheld beyond its default 2s.
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        var observedExit = controller.ChildExited ? await controller.ChildExit : (int?)null;
        Assert.False(controller.ChildExited,
            $"SIGTERM ended the controller while its acknowledged native turn was still held without correlated completion; child exit={observedExit}, running retained={controller.DiscussionState(1)["running"] is not null}, observed after 3s.");
        Assert.Equal(0, await controller.InvokeWrapper());
        Assert.Single(controller.Native.Turns);
        await controller.Finish(turn);
        Assert.Equal(0, await controller.ChildExit.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken));
        Assert.Null(controller.DiscussionState(1)["pending"]);
        Assert.Null(controller.DiscussionState(1)["running"]);
        Assert.Single(controller.Native.Turns);
    }
}

#if CONTROLLER_SHUTDOWN_CHILD
// Selected only by the isolated fixture build. Child mode reaches the real CLI directly, without runner signal handlers.
internal static class ControllerShutdownEntrypoint
{
    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 1 && args[0] == "--controller-shutdown-child")
        {
            if (!OperatingSystem.IsLinux()) return 1;
            using var provider = new RepositorySettingsTests.Provider();
            using var http = new HttpClient(provider, disposeHandler: false);
            return await BridgeCommand.Run(args[2..], http, args[1], TimeProvider.System);
        }
        // Keep the installed generated entrypoint's ordinary console/MTP routes for the isolated parent test invocation.
        if (args.Any(argument => argument is "--server" or "--internal-msbuild-node"))
            return await Xunit.MicrosoftTestingPlatform.TestPlatformTestFramework.RunAsync(args, SelfRegisteredExtensions.AddSelfRegisteredExtensions);
        return await Xunit.Runner.InProc.SystemConsole.ConsoleRunner.Run(args);
    }
}
#endif
