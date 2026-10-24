using System.Buffers.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace RepoAgent.Auth.Tests;

public sealed class SecretProtectionTests
{
    private const string Operation = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";

    // Stored ProtectedToken and ProtectedCompletion values are readable only under the exact chain that
    // protected them. These literals are the persisted format; they are never derived from ApprovalContract.
    public static TheoryData<string, string, string[]> TokenPurposes => new()
    {
        { "Legacy", "runtime", ["repoagent.issuer.v1", "123456", "epoch-7", Operation, "runtime"] },
        { "Agent", "verification", ["repoagent.issuer.v2", "repository-agent-variable", "repository-agent-approval-v2", "123456", "epoch-7", Operation, "verification"] },
        { "AgentClone", "runtime", ["repoagent.issuer.v2", "repository-agent-variable", "repository-agent-clone-approval-v2", "123456", "epoch-7", Operation, "runtime"] },
        { "Actions", "runtime", ["repoagent.issuer.v2", "repository-actions-variable", "repository-actions-approval-v3", "123456", "epoch-7", Operation, "runtime"] },
        { "ActionsClone", "verification", ["repoagent.issuer.v2", "repository-actions-variable", "repository-actions-clone-approval-v3", "123456", "epoch-7", Operation, "verification"] },
        { "ActionsSettings", "runtime", ["repoagent.issuer.v2", "repository-actions-variable", "repository-actions-settings-approval-v3", "123456", "epoch-7", Operation, "runtime"] },
    };

    public static TheoryData<string, string[]> CompletionPurposes => new()
    {
        { "Legacy", ["repoagent.enrollment.v1", "123456", "epoch-7", "pending-id"] },
        { "Agent", ["repoagent.enrollment.v2", "repository-agent-variable", "repository-agent-approval-v2", "123456", "epoch-7", "pending-id"] },
        { "AgentClone", ["repoagent.enrollment.v2", "repository-agent-variable", "repository-agent-clone-approval-v2", "123456", "epoch-7", "pending-id"] },
        { "Actions", ["repoagent.enrollment.v2", "repository-actions-variable", "repository-actions-approval-v3", "123456", "epoch-7", "pending-id"] },
        { "ActionsClone", ["repoagent.enrollment.v2", "repository-actions-variable", "repository-actions-clone-approval-v3", "123456", "epoch-7", "pending-id"] },
        { "ActionsSettings", ["repoagent.enrollment.v2", "repository-actions-variable", "repository-actions-settings-approval-v3", "123456", "epoch-7", "pending-id"] },
    };

    [Theory]
    [MemberData(nameof(TokenPurposes))]
    public void Stored_GitHub_tokens_keep_their_purpose_chain(string contract, string purpose, string[] expected)
    {
        var chain = Contract(contract).TokenPurpose("123456", "epoch-7", Operation, purpose);

        Assert.Equal<string>(expected, [chain.Root, .. chain.Chain]);
    }

    [Theory]
    [MemberData(nameof(CompletionPurposes))]
    public void Stored_setup_completions_keep_their_purpose_chain(string contract, string[] expected)
    {
        var chain = Contract(contract).CompletionPurpose("123456", "epoch-7", "pending-id");

        Assert.Equal<string>(expected, [chain.Root, .. chain.Chain]);
    }

    // The one golden test of this persisted format. The key and ciphertext were recorded at 32d6f73, before
    // the port existed, through IssuanceServices.Add and ApprovalContract.Actions.TokenProtector. Only key
    // storage differs from production: the recorded key replaces the Blob key ring and its Key Vault wrapping.
    [Fact]
    public void Token_protected_before_the_port_round_trips_through_the_production_key_ring()
    {
        var builder = WebApplication.CreateEmptyBuilder(new() { EnvironmentName = "Production" });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Broker:TableEndpoint"] = "https://golden.table.core.windows.net",
            ["Broker:TableName"] = "golden",
            ["Broker:DeploymentEpoch"] = "golden-epoch",
            ["Auth:SigningKeyUri"] = "https://golden.vault.azure.net/keys/app/version",
            ["Auth:DataProtectionKeyUri"] = "https://golden.vault.azure.net/keys/wrapping/version",
            ["Auth:DataProtectionBlobUri"] = "https://golden.blob.core.windows.net/data-protection/keyring.xml",
            ["Auth:DataProtectionApplicationName"] = "repoagent-auth-golden"
        });
        IssuanceServices.Add(builder);
        builder.Services.Configure<KeyManagementOptions>(options =>
        {
            options.XmlRepository = new RecordedKeyRing(XElement.Parse(RecordedKey));
            options.XmlEncryptor = null;
            options.AutoGenerateKeys = false;
        });
        using var services = builder.Services.BuildServiceProvider();
        var secrets = services.GetRequiredService<ISecretProtection>();
        var purpose = ApprovalContract.Actions.TokenPurpose("123456", "golden-epoch", Operation, "runtime");

        Assert.Equal(RecordedToken, secrets.Unprotect(purpose, RecordedCiphertext));
        var rewritten = secrets.Protect(purpose, RecordedToken);
        Assert.Equal(RecordedToken, secrets.Unprotect(purpose, rewritten));
        Assert.Equal(KeyHeader(RecordedCiphertext), KeyHeader(rewritten));
        Assert.Throws<SecretProtectionFailed>(() => secrets.Unprotect(
            ApprovalContract.ActionsClone.TokenPurpose("123456", "golden-epoch", Operation, "runtime"), RecordedCiphertext));
    }

    private static ApprovalContract Contract(string name) => name switch
    {
        "Legacy" => ApprovalContract.Legacy,
        "Agent" => ApprovalContract.Agent,
        "AgentClone" => ApprovalContract.AgentClone,
        "Actions" => ApprovalContract.Actions,
        "ActionsClone" => ApprovalContract.ActionsClone,
        "ActionsSettings" => ApprovalContract.ActionsSettings,
        _ => throw new ArgumentOutOfRangeException(nameof(name))
    };

    // Data Protection's format magic and key ID: a rewritten value must use the recorded format and key.
    private static string KeyHeader(string protectedSecret) => Convert.ToHexString(Base64Url.DecodeFromChars(protectedSecret)[..20]);

    private const string RecordedToken = "ghs_GoldenRuntimeTokenRecordedAtBaseline";
    private const string RecordedCiphertext = "CfDJ8Blr0LbQtodDv7Acve95877LwVPcNE_b3FwTdfa5MOPiRqwoYDjSjY8p2ZkGrIbrzO31qYtgZm0JaySLMIdpqDeCxpMGzxJRmq_QoIHEukw1LEGOIAFcqvzFZ88_gqHXW-CHnX3UkJ_n8hsVuWEAHA6ct9qUhW_1Qm0zeW7Lri7e";

    // A test-only key, stored unencrypted. Production keys are wrapped by the Key Vault key.
    private const string RecordedKey = """
        <key id="b6d06b19-b6d0-4387-bfb0-1cbdef79f3be" version="1">
          <creationDate>2026-10-24T18:08:53.19546Z</creationDate>
          <activationDate>2026-10-24T18:08:53.19546Z</activationDate>
          <expirationDate>2126-09-30T18:08:53.19546Z</expirationDate>
          <descriptor deserializerType="Microsoft.AspNetCore.DataProtection.AuthenticatedEncryption.ConfigurationModel.AuthenticatedEncryptorDescriptorDeserializer, Microsoft.AspNetCore.DataProtection, Version=10.0.0.0, Culture=neutral, PublicKeyToken=adb9793829ddae60">
            <descriptor>
              <encryption algorithm="AES_256_CBC" />
              <validation algorithm="HMACSHA256" />
              <masterKey p4:requiresEncryption="true" xmlns:p4="http://schemas.asp.net/2015/03/dataProtection">
                <value>Df6/shJ6jjfLFzy/70IK4GlvttvfecpTt61eWY44fYE5SENrZK0tu4RMtQ5GICJmA0Rg15co95v3Zj4ESrjneA==</value>
              </masterKey>
            </descriptor>
          </descriptor>
        </key>
        """;

    private sealed class RecordedKeyRing(XElement key) : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements() => [new XElement(key)];
        public void StoreElement(XElement element, string friendlyName) =>
            throw new InvalidOperationException("The golden key ring is read-only; no key may be generated.");
    }
}
