using Azure.Core.Cryptography;

namespace RepoAgent.Auth;

// The shared KeyResolver follows persisted key IDs verbatim. Only local emulator key-ring resolution may bind a
// historical loopback authority to the current endpoint; the core checks the configured key name and pinned version.
internal sealed class DevelopmentWrappingKeyResolver(IKeyEncryptionKeyResolver inner, Uri vault, Uri wrappingKey)
    : IKeyEncryptionKeyResolver
{
    public IKeyEncryptionKey Resolve(string keyId, CancellationToken cancellationToken = default) =>
        inner.Resolve(BrokerSettings.DevelopmentWrappingKey(vault, wrappingKey, keyId).AbsoluteUri, cancellationToken);

    public Task<IKeyEncryptionKey> ResolveAsync(string keyId, CancellationToken cancellationToken = default) =>
        inner.ResolveAsync(BrokerSettings.DevelopmentWrappingKey(vault, wrappingKey, keyId).AbsoluteUri, cancellationToken);
}
