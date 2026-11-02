using System.Text.Json;
using Azure;
using Azure.Data.Tables;
using Azure.Security.KeyVault.Keys;
using Azure.Storage.Blobs;
using AzureKeyVaultEmulator.Aspire.Client;
using Microsoft.Extensions.Configuration;

var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();
var table = new TableClient(configuration.GetConnectionString("tables"), "brokerstate");
var container = new BlobServiceClient(configuration.GetConnectionString("blobs")).GetBlobContainerClient("data-protection");
var vault = new Uri(configuration.GetConnectionString("keyvault")!);
if (!table.Uri.IsLoopback || !container.Uri.IsLoopback || !vault.IsLoopback || vault.Scheme != "https")
    throw new InvalidOperationException("Local initialization requires loopback emulators.");

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var ct = deadline.Token;
var created = await table.CreateIfNotExistsAsync(ct);
if (created.GetRawResponse().Status is 201 or 204)
{
    await table.AddEntityAsync(new TableEntity("broker:current-v2", "quota")
    {
        ["SchemaVersion"] = 2L,
        ["Repositories"] = 0L,
        ["Window"] = 0L,
        ["Admissions"] = 0L,
        ["Reads"] = 0L,
        ["Signs"] = 0L,
        ["Mints"] = 0L
    }, ct);
}
// Never initialize/reset metadata over an existing legacy Table. Recreate local emulator state for the cutover.
var quota = (await table.GetEntityAsync<TableEntity>("broker:current-v2", "quota", cancellationToken: ct)).Value;
if (quota.GetInt64("SchemaVersion") != 2L)
    throw new InvalidOperationException("Existing local quota schema is invalid; a fresh schema-2 Table is required.");
await container.CreateIfNotExistsAsync(cancellationToken: ct);
var keys = new KeyClient(vault, new EmulatedTokenCredential(vault.AbsoluteUri),
    new KeyClientOptions { DisableChallengeResourceVerification = true });
var sharedSigning = configuration["SharedSigningKeyUri"];
var developerSigning = !string.IsNullOrEmpty(sharedSigning);
// The shared URI is configuration only: this client can initialize keys only in the loopback emulator.
var signing = developerSigning ? sharedSigning! : LocalKeyUri(await GetOrCreateKey("development-signing"));
var wrapping = await GetOrCreateKey("wrapping");
var settings = new
{
    Broker = new
    {
        TableEndpoint = table.Uri.AbsoluteUri[..table.Uri.AbsoluteUri.LastIndexOf('/')],
        TableName = table.Name,
        DeploymentEpoch = "aspire-local-v1",
        BrokerOrigin = "https://localhost:7443",
        DevelopmentConnectionString = configuration.GetConnectionString("tables")
    },
    Auth = new
    {
        DevelopmentVaultUri = vault.AbsoluteUri,
        SigningKeyUri = signing,
        DevelopmentSigningCredential = developerSigning ? "AzureCli" : null,
        DevelopmentBlobConnectionString = configuration.GetConnectionString("blobs"),
        DataProtectionKeyUri = LocalKeyUri(wrapping),
        DataProtectionBlobUri = container.GetBlobClient("keyring.xml").Uri.AbsoluteUri,
        DataProtectionApplicationName = "repoagent-auth-aspire-local"
    }
};
var file = Path.GetFullPath(configuration["DevelopmentSettingsFile"]!);
if (OperatingSystem.IsWindows()) Directory.CreateDirectory(Path.GetDirectoryName(file)!);
else Directory.CreateDirectory(Path.GetDirectoryName(file)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
var writeOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
if (!OperatingSystem.IsWindows()) writeOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
await using (var stream = new FileStream(file, writeOptions))
    await JsonSerializer.SerializeAsync(stream, settings, new JsonSerializerOptions { WriteIndented = true }, ct);
Console.WriteLine("Local broker storage and pinned emulator keys are ready.");
Console.WriteLine(developerSigning
    ? "Shared signing is configured for the broker's Azure CLI credential; wrapping remains local."
    : "Using a generated development signing key; real GitHub App authentication requires SharedSigningKeyUri.");

async Task<KeyVaultKey> GetOrCreateKey(string name)
{
    try { return (await keys.GetKeyAsync(name, cancellationToken: ct)).Value; }
    catch (RequestFailedException exception) when (exception.Status == 404)
    { return (await keys.CreateRsaKeyAsync(new CreateRsaKeyOptions(name) { KeySize = 2048 }, ct)).Value; }
}

string LocalKeyUri(KeyVaultKey key)
{
    // Persisted emulator responses retain the authority from key creation. Isolated Aspire runs can publish a new
    // loopback port, so keep the returned key name/version at this client's current, already validated local vault.
    // Shared signing URIs never pass through this local-emulator adapter.
    var id = key.Id;
    if (!id.IsLoopback || id.Scheme != "https" || id.UserInfo.Length != 0 || id.Query.Length != 0 || id.Fragment.Length != 0)
        throw new InvalidOperationException("Emulated key identifiers must be bare loopback HTTPS URIs.");
    return new Uri(vault, id.AbsolutePath).AbsoluteUri;
}
