using System.CommandLine;
using System.Text.Json;

namespace RepoAgentController;

/// <summary>Named administration requests through the same private endpoint as activity; no provider access in the CLI.</summary>
static class RemoteCommand
{
    public static void Add(RootCommand root)
    {
        var remote = new Command("remote", "Request guarded native remote administration; currently unavailable until isolation and admission are qualified");
        foreach (var operation in new[] { RemoteOperation.Enable, RemoteOperation.Pair, RemoteOperation.Status,
            RemoteOperation.Clients, RemoteOperation.Revoke, RemoteOperation.Disable })
        {
            var state = new Option<FileInfo>("--state") { DefaultValueFactory = _ => new(".bridge-state.json"), Description = "Exact state path of the running controller" };
            var format = new Option<string>("--format") { DefaultValueFactory = _ => "text", Description = "Output text or deliberate local JSON" };
            var selectedClient = new Option<string?>("--client") { Required = true, Description = "Explicit client identifier to revoke; no implicit all-clients selection" };
            var command = new Command(operation.ToString().ToLowerInvariant(), "Requires administration permission; returns unavailable while native access cannot be safely enabled") { state, format };
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
                    else Console.Error.WriteLine($"{outcome.Outcome}: {outcome.Message}");
                    return 1; // Neither refusal nor unavailability confirms an administrative operation.
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
