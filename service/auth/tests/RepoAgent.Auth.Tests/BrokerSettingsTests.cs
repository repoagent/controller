using Xunit;

namespace RepoAgent.Auth.Tests;

// The production guards over the broker's settings, in the order the host applies them at startup: the local settings
// file, the issuance composition (with the broker options), the emulator's key ring, the broker options and the
// emulator's table.
public sealed class BrokerSettingsTests
{
    private const string Emulated = "http://127.0.0.1:10000/devstoreaccount1/data-protection/keyring.xml";
    private const string EmulatedTable = "http://127.0.0.1:10002/devstoreaccount1/readiness";
    // The table a remote account's connection string names: the configured endpoint's, so only the loopback guard refuses it.
    private const string RemoteTable = "https://readiness.table.core.windows.net/readiness";

    public enum Startup { Ready, NotReady, Refused }

    // The whole startup sequence, for what only it shows: readiness (/health/ready answers 200 when ready, 503 when not),
    // and Development storage only on a loopback table endpoint. Each malformed setting has its own theory.
    [Theory]
    [InlineData(null, Startup.Ready)]
    [InlineData("missing-signing", Startup.NotReady)]
    [InlineData("missing-origin", Startup.NotReady)]
    [InlineData("revoke-only", Startup.NotReady)]
    [InlineData("local-table-production", Startup.Refused)]
    [InlineData("remote-development-storage", Startup.Refused)]
    [InlineData("local-development-storage", Startup.Ready)]
    [InlineData("local-vault-development", Startup.Ready)]
    public void Only_a_broker_that_issues_and_enrolls_is_ready(string? scenario, Startup expected)
    {
        var (settings, environment) = Scenario(scenario);
        string? Setting(string name) => settings.GetValueOrDefault(name);
        bool Start()
        {
            _ = BrokerSettings.DevelopmentSettingsFile(Setting, environment);
            var issuance = BrokerSettings.Issuance(Setting, environment);
            issuance?.RequireEmulatedKeyRing(new(Emulated));
            var options = BrokerSettings.Options(Setting, environment);
            // The table the Development connection's client names; the adapter reads it from the TableClient.
            if (BrokerSettings.DevelopmentConnection(Setting, environment) is not null)
                options.RequireEmulatedTable(new(scenario == "remote-development-storage" ? RemoteTable : EmulatedTable));
            return BrokerSettings.Ready(issuance);
        }

        if (expected == Startup.Refused) Assert.Throws<InvalidOperationException>(() => Start());
        else Assert.Equal(expected == Startup.Ready, Start());
    }

    // Uri equality ignores user information and fragments, so the emulator's table is also checked for them.
    [Theory]
    [InlineData(EmulatedTable, true)]
    [InlineData("http://127.0.0.1:10002/devstoreaccount1/other", false)]
    [InlineData("http://user:secret@127.0.0.1:10002/devstoreaccount1/readiness", false)]
    [InlineData(EmulatedTable + "?sv=private", false)]
    [InlineData(EmulatedTable + "#fragment", false)]
    public void The_emulators_table_must_be_the_configured_one(string table, bool accepted)
    {
        var (settings, environment) = Scenario("local-development-storage");
        var options = BrokerSettings.Options(settings.GetValueOrDefault, environment);
        if (accepted) options.RequireEmulatedTable(new(table));
        else Assert.Throws<InvalidOperationException>(() => options.RequireEmulatedTable(new(table)));
    }

    // Uri equality ignores user information and fragments, so the emulator's key ring is also checked for them.
    [Theory]
    [InlineData(Emulated, Emulated, true)]
    [InlineData(Emulated, "http://127.0.0.1:10000/devstoreaccount1/data-protection/other.xml", false)]
    [InlineData("http://user:secret@127.0.0.1:10000/devstoreaccount1/data-protection/keyring.xml", Emulated, false)]
    [InlineData(Emulated + "#fragment", Emulated, false)]
    [InlineData(Emulated + "?query", Emulated + "?query", false)]
    [InlineData("https://account.blob.core.windows.net/data-protection/keyring.xml", "https://account.blob.core.windows.net/data-protection/keyring.xml", false)]
    public void The_emulators_key_ring_must_be_the_configured_one_on_loopback(string configured, string emulated, bool accepted)
    {
        var (settings, environment) = Scenario("local-development-storage");
        settings["Auth:DataProtectionBlobUri"] = configured;
        var issuance = BrokerSettings.Issuance(settings.GetValueOrDefault, environment)!;
        if (accepted) issuance.RequireEmulatedKeyRing(new(emulated));
        else Assert.Throws<InvalidOperationException>(() => issuance.RequireEmulatedKeyRing(new(emulated)));
        (issuance with { DevelopmentStorage = null }).RequireEmulatedKeyRing(new("https://elsewhere.example/keyring.xml"));
    }

    [Theory]
    [InlineData("Broker:TableEndpoint", "http://readiness.table.core.windows.net")]
    [InlineData("Broker:TableEndpoint", "https://readiness.table.core.windows.net/table")]
    [InlineData("Broker:TableEndpoint", "https://user:secret@readiness.table.core.windows.net")]
    [InlineData("Broker:TableEndpoint", "https://readiness.table.core.windows.net/?sv=private")]
    [InlineData("Broker:TableEndpoint", "https://readiness.table.core.windows.net/#fragment")]
    [InlineData("Broker:TableEndpoint", "http://127.0.0.1:10002/devstoreaccount1")]
    [InlineData("Broker:TableName", "ab")]
    [InlineData("Broker:TableName", "1readiness")]
    [InlineData("Broker:TableName", "read-iness")]
    [InlineData("Broker:DeploymentEpoch", "")]
    [InlineData("Broker:DeploymentEpoch", "readiness test")]
    [InlineData("Broker:ManagedIdentityClientId", "readiness")]
    [InlineData("Broker:BrokerOrigin", "http://broker.example.test")]
    [InlineData("Broker:BrokerOrigin", "https://203.0.113.7")]
    [InlineData("Broker:BrokerOrigin", "https://user@broker.example.test")]
    [InlineData("Broker:BrokerOrigin", "https://broker.example.test/path")]
    [InlineData("Broker:BrokerOrigin", "https://broker.example.test/?query")]
    [InlineData("Broker:BrokerOrigin", "https://broker.example.test/#fragment")]
    public void Broker_options_name_only_the_resources_they_claim(string name, string value)
    {
        var (settings, _) = Scenario(null);
        settings["Broker:ManagedIdentityClientId"] = "11111111-1111-1111-1111-111111111111";
        Assert.NotNull(BrokerOptions.Read(settings.GetValueOrDefault).BrokerOrigin);
        settings[name] = value;
        Assert.Throws<InvalidOperationException>(() => BrokerOptions.Read(settings.GetValueOrDefault));
    }

    // With Development storage the table endpoint may instead be the emulator's: plain HTTP on loopback, with an account path.
    [Theory]
    [InlineData("http://127.0.0.1:10002/devstoreaccount1", true)]
    [InlineData("http://localhost:10002/devstoreaccount1", true)]
    [InlineData("http://readiness.table.core.windows.net/devstoreaccount1", false)]
    [InlineData("https://127.0.0.1:10002/devstoreaccount1", false)]
    [InlineData("http://127.0.0.1:10002/", false)]
    [InlineData("http://127.0.0.1:10002/devstore-account1", false)]
    public void Development_storage_may_name_only_a_loopback_emulators_table_endpoint(string endpoint, bool accepted)
    {
        var (settings, _) = Scenario(null);
        settings["Broker:TableEndpoint"] = endpoint;
        BrokerOptions Read() => BrokerOptions.Read(settings.GetValueOrDefault, allowDevelopmentLoopback: true);
        if (accepted) Assert.Equal(new Uri(endpoint), Read().TableEndpoint);
        else Assert.Throws<InvalidOperationException>(Read);
    }

    [Fact]
    public void Broker_options_bound_their_names_and_default_the_request_limits()
    {
        var (settings, _) = Scenario(null);
        BrokerOptions Read() => BrokerOptions.Read(settings.GetValueOrDefault);
        Assert.Equal((120, 8, 15), (Read().RequestLimitPerMinute, Read().ConcurrentRequestLimit, Read().RequestTimeoutSeconds));
        settings["Broker:TableName"] = "t" + new string('1', 62);
        settings["Broker:DeploymentEpoch"] = new string('e', 128);
        Assert.Equal((63, 128), (Read().TableName.Length, Read().DeploymentEpoch.Length));
        settings["Broker:TableName"] += "1";
        Assert.Throws<InvalidOperationException>(Read);
        settings["Broker:TableName"] = "readiness";
        settings["Broker:DeploymentEpoch"] += "e";
        Assert.Throws<InvalidOperationException>(Read);
    }

    [Theory]
    [InlineData("Broker:DevelopmentSettingsFile")]
    [InlineData("Broker:DevelopmentConnectionString")]
    [InlineData("Broker:Cleanup:DevelopmentConnectionString")]
    [InlineData("Auth:DevelopmentBlobConnectionString")]
    public void Local_settings_and_storage_exist_only_in_Development(string name)
    {
        var (settings, _) = Scenario(null);
        settings[name] = "local";
        string? Read(string? environment) => name switch
        {
            "Broker:DevelopmentSettingsFile" => BrokerSettings.DevelopmentSettingsFile(settings.GetValueOrDefault, environment),
            "Auth:DevelopmentBlobConnectionString" => BrokerSettings.Issuance(settings.GetValueOrDefault, environment)?.DevelopmentStorage,
            _ => BrokerSettings.DevelopmentConnection(settings.GetValueOrDefault, environment),
        };

        Assert.Equal("local", Read("Development"));
        Assert.Throws<InvalidOperationException>(() => Read("Production"));
        Assert.Throws<InvalidOperationException>(() => Read(null));
        settings.Clear();
        Assert.Null(Read("Production"));
    }

    [Theory]
    [InlineData(null, "Auth:SigningKeyUri", "https://readiness.vault.azure.net/keys/signing")]
    [InlineData(null, "Auth:SigningKeyUri", "https://readiness.vault.azure.net/secrets/signing/version")]
    [InlineData(null, "Auth:SigningKeyUri", "http://readiness.vault.azure.net/keys/signing/version")]
    [InlineData(null, "Auth:DataProtectionKeyUri", "https://user:secret@readiness.vault.azure.net/keys/wrapping/version")]
    [InlineData(null, "Auth:DataProtectionKeyUri", "https://readiness.vault.azure.net/keys/wrapping/version?api-version=1")]
    [InlineData(null, "Auth:DataProtectionBlobUri", "https://readiness.blob.core.windows.net/data-protection/keyring.xml#fragment")]
    [InlineData(null, "Auth:DataProtectionBlobUri", "http://readiness.blob.core.windows.net/data-protection/keyring.xml")]
    [InlineData(null, "Auth:DataProtectionApplicationName", " ")]
    [InlineData("local-vault-development", "Auth:SigningKeyUri", "https://readiness.vault.azure.net/keys/signing/version")]
    [InlineData("local-vault-development", "Auth:DataProtectionKeyUri", "https://readiness.vault.azure.net/keys/wrapping/version")]
    [InlineData("local-vault-development", "AZURE_AUTHORITY_HOST", "https://localhost:4998")]
    public void Key_Vault_keys_the_key_ring_and_the_local_vault_must_be_what_they_claim(string? scenario, string name, string value)
    {
        var (settings, environment) = Scenario(scenario);
        Assert.NotNull(BrokerSettings.Issuance(settings.GetValueOrDefault, environment));
        settings[name] = value;
        Assert.Throws<InvalidOperationException>(() => BrokerSettings.Issuance(settings.GetValueOrDefault, environment));
    }

    // The keys and the credential authority are at the vault in every row; IsDevelopment matches the environment in any case.
    [Theory]
    [InlineData("https://localhost:4997", "Development", true)]
    [InlineData("https://localhost:4997", "development", true)]
    [InlineData("https://localhost:4997", "Production", false)]
    [InlineData("https://vault.example.test", "Development", false)]
    [InlineData("https://localhost:4997/vault", "Development", false)]
    [InlineData("http://localhost:4997", "Development", false)]
    public void The_emulated_Key_Vault_is_a_loopback_HTTPS_origin_in_Development(string vault, string environment, bool accepted)
    {
        var (settings, _) = Scenario(null);
        settings["Auth:DevelopmentVaultUri"] = settings["AZURE_AUTHORITY_HOST"] = vault;
        settings["Auth:SigningKeyUri"] = new Uri(new Uri(vault), "/keys/signing/version").AbsoluteUri;
        settings["Auth:DataProtectionKeyUri"] = new Uri(new Uri(vault), "/keys/wrapping/version").AbsoluteUri;
        IssuanceSettings? Read() => BrokerSettings.Issuance(settings.GetValueOrDefault, environment);
        if (accepted) Assert.Equal(new Uri(vault), Read()!.DevelopmentVault);
        else Assert.Throws<InvalidOperationException>(Read);
    }

    // Without a signing key the broker only revokes, but the key-ring settings it was given must still be well formed.
    [Theory]
    [InlineData("Auth:DataProtectionKeyUri", "https://readiness.vault.azure.net/keys/wrapping")]
    [InlineData("Auth:DataProtectionBlobUri", "http://readiness.blob.core.windows.net/data-protection/keyring.xml")]
    [InlineData("Auth:DataProtectionApplicationName", " ")]
    public void A_composition_that_only_revokes_still_rejects_malformed_key_ring_settings(string name, string value)
    {
        var (settings, environment) = Scenario("missing-signing");
        Assert.Null(BrokerSettings.Issuance(settings.GetValueOrDefault, environment));
        settings[name] = value;
        Assert.Throws<InvalidOperationException>(() => BrokerSettings.Issuance(settings.GetValueOrDefault, environment));
    }

    [Fact]
    public void An_application_name_longer_than_128_characters_names_no_shared_key_ring()
    {
        var (settings, environment) = Scenario(null);
        settings["Auth:DataProtectionApplicationName"] = new string('a', 128);
        Assert.NotNull(BrokerSettings.Issuance(settings.GetValueOrDefault, environment));
        settings["Auth:DataProtectionApplicationName"] = new string('a', 129);
        Assert.Throws<InvalidOperationException>(() => BrokerSettings.Issuance(settings.GetValueOrDefault, environment));
        settings.Remove("Auth:SigningKeyUri");
        Assert.Throws<InvalidOperationException>(() => BrokerSettings.Issuance(settings.GetValueOrDefault, environment));
    }

    [Theory]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/")]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://WestEurope-5.in.ApplicationInsights.azure.com/;LiveEndpoint=https://westeurope.livediagnostics.monitor.azure.com/;ApplicationId=33333333-3333-3333-3333-333333333333")]
    public void Monitoring_names_an_instrumentation_key_an_ingestion_origin_and_a_client_ID(string connection)
    {
        Assert.Equal(new MonitoringSettings(connection, "11111111-1111-1111-1111-111111111111"),
            BrokerSettings.Monitoring(connection, "11111111-1111-1111-1111-111111111111"));
        Assert.Null(BrokerSettings.Monitoring(null, "not-a-client-id"));
        Assert.Null(BrokerSettings.Monitoring("", null));
    }

    [Theory]
    [InlineData("private-malformed", "11111111-1111-1111-1111-111111111111")]
    [InlineData("instrumentationkey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/", "11111111-1111-1111-1111-111111111111")]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/;Authorization=private", "11111111-1111-1111-1111-111111111111")]
    [InlineData("InstrumentationKey=private;IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/", "11111111-1111-1111-1111-111111111111")]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222", "11111111-1111-1111-1111-111111111111")]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=http://westeurope-5.in.applicationinsights.azure.com/", "11111111-1111-1111-1111-111111111111")]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://private.example/", "11111111-1111-1111-1111-111111111111")]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://westeurope.livediagnostics.monitor.azure.com/", "11111111-1111-1111-1111-111111111111")]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com:8443/", "11111111-1111-1111-1111-111111111111")]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://user:secret@westeurope-5.in.applicationinsights.azure.com/", "11111111-1111-1111-1111-111111111111")]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/?private=1", "11111111-1111-1111-1111-111111111111")]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/#private", "11111111-1111-1111-1111-111111111111")]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/v2/track", "11111111-1111-1111-1111-111111111111")]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/", null)]
    [InlineData("InstrumentationKey=22222222-2222-2222-2222-222222222222;IngestionEndpoint=https://westeurope-5.in.applicationinsights.azure.com/", "private")]
    public void Malformed_monitoring_is_rejected(string connection, string? clientId) =>
        Assert.ThrowsAny<Exception>(() => BrokerSettings.Monitoring(connection, clientId));

    // The readiness test's settings: a fully configured production broker, then each scenario's change.
    private static (Dictionary<string, string?> Settings, string Environment) Scenario(string? scenario)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Broker:TableEndpoint"] = "https://readiness.table.core.windows.net",
            ["Broker:TableName"] = "readiness",
            ["Broker:DeploymentEpoch"] = "readiness-test",
            ["Broker:BrokerOrigin"] = "https://broker.example.test",
            ["Auth:SigningKeyUri"] = "https://readiness.vault.azure.net/keys/signing/version",
            ["Auth:DataProtectionKeyUri"] = "https://readiness.vault.azure.net/keys/wrapping/version",
            ["Auth:DataProtectionBlobUri"] = "https://readiness.blob.core.windows.net/data-protection/keyring.xml",
            ["Auth:DataProtectionApplicationName"] = "readiness-test",
        };
        var environment = "Production";
        if (scenario is "missing-signing") settings.Remove("Auth:SigningKeyUri");
        // remote-development-storage only revokes, so the key ring's guard can't refuse it before the table's.
        if (scenario is "revoke-only" or "remote-development-storage")
            foreach (var name in new[] { "SigningKeyUri", "DataProtectionKeyUri", "DataProtectionBlobUri", "DataProtectionApplicationName" })
                settings.Remove("Auth:" + name);
        if (scenario is "missing-origin" or "revoke-only") settings.Remove("Broker:BrokerOrigin");
        if (scenario is "local-table-production" or "local-development-storage")
            settings["Broker:TableEndpoint"] = "http://127.0.0.1:10002/devstoreaccount1";
        if (scenario is "remote-development-storage" or "local-development-storage")
        {
            environment = "Development";
            settings["Broker:DevelopmentConnectionString"] = scenario == "local-development-storage"
                ? "UseDevelopmentStorage=true" : "DefaultEndpointsProtocol=https;AccountName=readiness;AccountKey=cmVhZGluZXNz";
            if (scenario == "local-development-storage") settings["Auth:DataProtectionBlobUri"] = Emulated;
        }
        if (scenario == "local-vault-development")
        {
            environment = "Development";
            settings["Auth:DevelopmentVaultUri"] = "https://localhost:4997";
            settings["Auth:SigningKeyUri"] = "https://localhost:4997/keys/signing/version";
            settings["Auth:DataProtectionKeyUri"] = "https://localhost:4997/keys/wrapping/version";
            settings["AZURE_AUTHORITY_HOST"] = "https://localhost:4997/";
        }
        return (settings, environment);
    }
}
