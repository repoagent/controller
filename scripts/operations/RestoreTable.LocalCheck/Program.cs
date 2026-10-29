using System.Diagnostics;
using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using RepoAgent.RestoreTable;

if (OperatingSystem.IsWindows() || args.Length != 1) throw new InvalidOperationException("Supply one native Linux/WSL loopback Azurite Table endpoint.");
var endpoint = RestoreRun.LocalEndpoint(args[0]);
var run = RestoreRun.Parse("20261029-abcdef");
var directory = Directory.CreateTempSubdirectory("repoagent-r230-table-").FullName;
File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var ct = deadline.Token;
var service = new TableServiceClient(endpoint, new TableSharedKeyCredential("devstoreaccount1", RestoreCommand.EmulatorKey));
var source = service.GetTableClient(RestoreRun.SourceTable);
var target = service.GetTableClient(RestoreRun.TargetTable);
var ownedSource = false;
var ownedTarget = false;
var privateCanary = "synthetic-private-row-canary";
try
{
    await Command("initialize", directory, expected: 0);
    ownedSource = true;
    var quota = (await source.GetEntityAsync<TableEntity>("global:enrollment-v1", "quota", cancellationToken: ct)).Value;
    Require(quota["Count"] is long and 0L, "Initialization did not persist Int64 quota values.");
    quota["Count"] = 7L; // Synthetic local fixture, never a live accounting edit.
    await source.UpdateEntityAsync(quota, quota.ETag, TableUpdateMode.Replace, ct);
    var secondDirectory = Path.Combine(directory, "retry");
    Directory.CreateDirectory(secondDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    await Command("initialize", secondDirectory, expected: 1);
    Require((await source.GetEntityAsync<TableEntity>("global:enrollment-v1", "quota", cancellationToken: ct)).Value.GetInt64("Count") == 7L,
        "Existing source accounting was reset.");
    for (var i = 0; i < 70; i++)
        await source.AddEntityAsync(new TableEntity("repository:123", $"fixture:{i:D3}")
        {
            ["Int64"] = 0L,
            ["Int32"] = 0,
            ["Text"] = privateCanary,
            ["a"] = "lower",
            ["Z"] = "upper",
            ["Clock"] = new DateTimeOffset(2026, 10, 29, 1, 2, 3, TimeSpan.Zero),
            ["Binary"] = new byte[] { 0, 255 },
            ["Revoked"] = true,
            ["State"] = "revoked",
            ["Double"] = 1.25,
            ["Guid"] = Guid.Parse("12345678-1234-1234-1234-123456789abc")
        }, ct);
    PrivateWrite("fence.json", JsonSerializer.SerializeToUtf8Bytes(new OfflineFence(1, run.Id, endpoint.AbsoluteUri,
        true, true, true, DateTimeOffset.UtcNow, ["Synthetic local fixture; no GitHub capability exists."]), JsonSerializerOptions.Web));
    var openDirectory = Path.Combine(directory, "open");
    Directory.CreateDirectory(openDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    new PrivateArtifacts(openDirectory).Write("fence.json", new PrivateArtifacts(directory).Read("fence.json"));
    File.SetUnixFileMode(openDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead);
    await Command("snapshot", openDirectory, expected: 1);
    Require(!File.Exists(Path.Combine(openDirectory, "snapshot.json")), "Snapshot was published through a nonprivate directory.");
    File.SetUnixFileMode(openDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    var linkedDirectory = Path.Combine(directory, "linked");
    Directory.CreateDirectory(linkedDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    File.CreateSymbolicLink(Path.Combine(linkedDirectory, "fence.json"), Path.Combine(directory, "fence.json"));
    await Command("snapshot", linkedDirectory, expected: 1);
    var snapshot = await Command("snapshot", directory, expected: 0);
    using (var result = JsonDocument.Parse(snapshot))
        Require(result.RootElement.GetProperty("rows").GetInt32() == 72 && result.RootElement.GetProperty("pages").GetInt32() >= 3,
            "Snapshot did not traverse every real provider page.");
    await Command("snapshot", directory, expected: 1); // Private snapshot cannot be overwritten.
    await service.CreateTableAsync(RestoreRun.TargetTable, ct);
    ownedTarget = true;
    await target.AddEntityAsync(new TableEntity("sentinel", "keep") { ["Value"] = 42L }, ct);
    await Command("restore", directory, expected: 1);
    Require((await target.GetEntityAsync<TableEntity>("sentinel", "keep", cancellationToken: ct)).Value.GetInt64("Value") == 42L,
        "Existing target was overwritten.");
    await service.DeleteTableAsync(RestoreRun.TargetTable, ct); // Delete only this harness's synthetic target.
    ownedTarget = false;
    await Command("restore", directory, expected: 0);
    ownedTarget = true;
    await Command("verify", directory, expected: 0);
    var restored = (await target.GetEntityAsync<TableEntity>("repository:123", "fixture:000", cancellationToken: ct)).Value;
    Require(restored["Int64"] is long && restored["Int32"] is int && restored.GetBoolean("Revoked") == true
        && restored.GetString("State") == "revoked" && restored.GetDateTimeOffset("Clock") == new DateTimeOffset(2026, 10, 29, 1, 2, 3, TimeSpan.Zero)
        && restored.GetBinary("Binary").SequenceEqual(new byte[] { 0, 255 }), "Restored properties or EDM types changed.");
    await Command("restore", directory, expected: 1);
    Console.WriteLine("Local operator journey passed: fresh Int64 initialization, refusal of existing source/target, complete pagination, private snapshot no-overwrite, typed Add-only restore and verification. Synthetic fixtures prove no live capability or Azure RBAC behavior.");
}
finally
{
    if (ownedSource || File.Exists(Path.Combine(directory, "source-created.json"))) await service.DeleteTableAsync(RestoreRun.SourceTable, ct);
    if (ownedTarget || File.Exists(Path.Combine(directory, "target-created.json"))) await service.DeleteTableAsync(RestoreRun.TargetTable, ct);
    Directory.Delete(directory, recursive: true);
}

async Task<string> Command(string operation, string stateDirectory, int expected)
{
    using var process = new Process
    {
        StartInfo = new ProcessStartInfo("dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false }
    };
    foreach (var argument in new[] { typeof(RestoreCommand).Assembly.Location, operation, "--run-id", run.Id,
        "--directory", stateDirectory, "--local-table-endpoint", endpoint.AbsoluteUri }) process.StartInfo.ArgumentList.Add(argument);
    if (!process.Start()) throw new InvalidOperationException("Local operator command did not start.");
    var output = process.StandardOutput.ReadToEndAsync(ct);
    var error = process.StandardError.ReadToEndAsync(ct);
    try { await process.WaitForExitAsync(ct); }
    catch
    {
        if (!process.HasExited) process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync(CancellationToken.None);
        throw;
    }
    var text = await output;
    var diagnostic = await error;
    Require(!(text + diagnostic).Contains(privateCanary, StringComparison.Ordinal), "Private row material reached operator output.");
    Require(process.ExitCode == expected, $"Local operator {operation} had an unexpected outcome: {diagnostic}");
    return text;
}

void PrivateWrite(string name, byte[] bytes) => new PrivateArtifacts(directory).Write(name, bytes);
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
