using System.CommandLine;
using System.Text.Json;

namespace RepoAgentController;

/// <summary>Named administration requests through the same private endpoint as activity; no provider access in the CLI.</summary>
static class RemoteCommand
{
    public static void Add(RootCommand root)
    {
        var remote = new Command("remote", "Manage native access on the selected controller runtime");
        foreach (var operation in new[] { RemoteOperation.Enable, RemoteOperation.Pair, RemoteOperation.Status,
            RemoteOperation.Clients, RemoteOperation.Revoke, RemoteOperation.Disable })
        {
            var state = new Option<FileInfo>("--state") { DefaultValueFactory = _ => new(".bridge-state.json"), Description = "Exact state path of the running controller" };
            var format = new Option<string>("--format") { DefaultValueFactory = _ => "text", Description = "Output text or deliberate local JSON" };
            var selectedClient = new Option<string?>("--client") { Required = true, Description = "Explicit client identifier to revoke; no implicit all-clients selection" };
            var command = new Command(operation.ToString().ToLowerInvariant(), "Manage native owner access through the private controller endpoint") { state, format };
            if (operation == RemoteOperation.Revoke) command.Options.Add(selectedClient);
            command.SetAction(async (result, ct) =>
            {
                var output = result.GetValue(format);
                var clientId = operation == RemoteOperation.Revoke ? result.GetValue(selectedClient) : null;
                if (output is not ("text" or "json") || clientId?.Length > 512 ||
                    (operation == RemoteOperation.Revoke && string.IsNullOrWhiteSpace(clientId)))
                { Console.Error.WriteLine("Choose --format text|json and an explicit --client ID for revoke."); return 1; }
                try
                {
                    using var client = new LocalControllerClient(result.GetValue(state)!.FullName);
                    var outcome = await client.Remote(operation, clientId, ct);
                    if (output == "json") Console.WriteLine(JsonSerializer.Serialize(outcome, ActivityJson.Default.RemoteAccessResult));
                    else
                    {
                        var writer = outcome.Outcome == RemoteAccessOutcome.Success ? Console.Out : Console.Error;
                        writer.WriteLine($"{outcome.Outcome}: {outcome.Message}");
                        if (outcome.Status is { } status) writer.WriteLine($"desired={(status.DesiredEnabled ? "enabled" : "disabled")} actual={status.Connection.ToString().ToLowerInvariant()} generation={status.Generation} unconfirmed={status.Unconfirmed.ToString().ToLowerInvariant()}");
                        if (outcome.Pair is { } pair) writer.WriteLine($"code={pair.Code} expires={pair.ExpiresAt:O}");
                        foreach (var item in outcome.Clients ?? []) writer.WriteLine($"{item.Id}\t{item.Name}\t{item.Device}\t{item.Platform}\t{item.LastSeenAt:O}");
                    }
                    return outcome.Outcome == RemoteAccessOutcome.Success ? 0 : 1;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                { Console.Error.WriteLine("Remote command canceled; the operation is unconfirmed."); return 1; }
                catch (LocalProtocolException error) { Console.Error.WriteLine(error.Message); return 1; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException or
                    JsonException or OperationCanceledException or ArgumentException)
                { Console.Error.WriteLine("Remote command unavailable: select a running controller with a private local endpoint. Install matching repoagent clients/images and restart the selected controller after upgrading."); return 1; }
            });
            remote.Subcommands.Add(command);
        }
        root.Subcommands.Add(remote);
    }
}
