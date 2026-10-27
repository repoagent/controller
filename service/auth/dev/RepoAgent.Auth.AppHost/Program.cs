using AzureKeyVaultEmulator.Aspire.Hosting;

var builder = DistributedApplication.CreateBuilder(args);
if (builder.ExecutionContext.IsPublishMode)
    throw new InvalidOperationException("This AppHost is for local development. Deploy Azure resources with Bicep.");
if (builder.Configuration["SigningKeyPemPath"] is not null)
    throw new InvalidOperationException("PEM import is no longer supported. Set SharedSigningKeyUri to the pinned shared Key Vault key URI.");
var localDirectory = Path.Combine(builder.AppHostDirectory, ".local");
if (OperatingSystem.IsWindows()) Directory.CreateDirectory(localDirectory);
else Directory.CreateDirectory(localDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

var storage = builder.AddAzureStorage("storage").RunAsEmulator(emulator => emulator
    .WithImageTag("3.35.0").WithBlobPort(27000).WithQueuePort(27001).WithTablePort(27002).WithDataVolume());
var tables = storage.AddTables("tables");
var blobs = storage.AddBlobs("blobs");
var vault = builder.AddAzureKeyVaultEmulator("keyvault", new KeyVaultEmulatorOptions
{
    Persist = true,
    Port = 4997,
    UseDotnetDevCerts = true,
    LocalCertificatePath = Path.Combine(localDirectory, "keyvault")
});
var settingsFile = Path.Combine(localDirectory, "broker.json");
var initialize = builder.AddProject<Projects.RepoAgent_Auth_DevInit>("initialize")
    .WithReference(tables).WithReference(blobs).WithReference(vault)
    .WithEnvironment("DevelopmentSettingsFile", settingsFile)
    .WithEnvironment("SharedSigningKeyUri", builder.Configuration["SharedSigningKeyUri"] ?? "")
    .WaitFor(storage).WaitFor(vault);

builder.AddProject<Projects.RepoAgent_Auth>("auth", launchProfileName: "https")
    .WithEnvironment("Broker__DevelopmentSettingsFile", settingsFile)
    .WithAzureKeyVaultEmulatorCredentials(vault)
    .WithHttpHealthCheck("/health/ready", endpointName: "https")
    .WaitForCompletion(initialize);

builder.AddProject<Projects.RepoAgent_Auth>("cleanup", launchProfileName: null)
    .WithArgs("cleanup", "--watch")
    .WithEnvironment("DOTNET_ENVIRONMENT", "Development")
    .WithEnvironment("Broker__DevelopmentSettingsFile", settingsFile)
    .WaitForCompletion(initialize);

builder.Build().Run();
