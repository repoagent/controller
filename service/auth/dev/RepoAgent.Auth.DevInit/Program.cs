using System.Security.Cryptography;
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
    await table.AddEntityAsync(new TableEntity("global:enrollment-v1", "quota")
    {
        ["RetentionSchemaVersion"] = 1L, ["CandidateCount"] = 0L,
        ["RetainedEnrollmentCount"] = 0L, ["Count"] = 0L
    }, ct);
    await table.AddEntityAsync(new TableEntity("global:issuance-v1", "quota")
    {
        ["RetentionSchemaVersion"] = 1L, ["RetainedIssuanceCount"] = 0L,
        ["Window"] = 0L, ["Admissions"] = 0L, ["Reads"] = 0L, ["Signs"] = 0L, ["Mints"] = 0L, ["Slots"] = 0L
    }, ct);
}
// Missing metadata in an existing store is an error, never a reason to reset accounting.
foreach (var partition in new[] { "global:enrollment-v1", "global:issuance-v1" })
{
    var row = (await table.GetEntityAsync<TableEntity>(partition, "quota", cancellationToken: ct)).Value;
    if (row.GetInt64("RetentionSchemaVersion") != 1L)
        throw new InvalidOperationException("Existing local quota schema is invalid.");
}
await container.CreateIfNotExistsAsync(cancellationToken: ct);
var keys = new KeyClient(vault, new EmulatedTokenCredential(vault.AbsoluteUri),
    new KeyClientOptions { DisableChallengeResourceVerification = true });
var pemPath = configuration["SigningKeyPemPath"];
var signing = await GetOrCreateKey(string.IsNullOrEmpty(pemPath) ? "development-signing" : "app-signing", pemPath);
var wrapping = await GetOrCreateKey("wrapping", null);
var settings = new
{
    Broker = new
    {
        TableEndpoint = table.Uri.AbsoluteUri[..table.Uri.AbsoluteUri.LastIndexOf('/')],
        TableName = table.Name, DeploymentEpoch = "aspire-local-v1", BrokerOrigin = "https://localhost:7443",
        DevelopmentConnectionString = configuration.GetConnectionString("tables")
    },
    Auth = new
    {
        DevelopmentVaultUri = vault.AbsoluteUri, SigningKeyUri = signing.Id.AbsoluteUri,
        DevelopmentBlobConnectionString = configuration.GetConnectionString("blobs"),
        DataProtectionKeyUri = wrapping.Id.AbsoluteUri,
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
Console.WriteLine(string.IsNullOrEmpty(pemPath)
    ? "Using a generated development signing key; real GitHub App authentication requires an explicitly supplied App key."
    : "Using the explicitly supplied GitHub App signing key in the local vault.");

async Task<KeyVaultKey> GetOrCreateKey(string name, string? pemPath)
{
    if (!string.IsNullOrEmpty(pemPath))
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(await File.ReadAllTextAsync(pemPath, ct));
        return (await keys.ImportKeyAsync(name, new JsonWebKey(rsa, includePrivateParameters: true), cancellationToken: ct)).Value;
    }
    try { return (await keys.GetKeyAsync(name, cancellationToken: ct)).Value; }
    catch (RequestFailedException exception) when (exception.Status == 404)
    { return (await keys.CreateRsaKeyAsync(new CreateRsaKeyOptions(name) { KeySize = 2048 }, ct)).Value; }
}
