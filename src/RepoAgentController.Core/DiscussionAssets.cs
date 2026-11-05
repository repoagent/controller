using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace RepoAgentController;

/// <summary>One bounded, immutable image or editable diagram source.</summary>
public sealed record DiscussionAsset
{
    public const int MaximumBytes = 10 * 1024 * 1024;
    public ReadOnlyMemory<byte> Content { get; }
    public string Extension { get; }
    public string Hash { get; }
    public bool IsImage => Extension is "png" or "jpg";

    public DiscussionAsset(string extension, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length is 0 or > MaximumBytes) throw new ArgumentException("Assets must contain 1 byte to 10 MiB");
        Extension = extension.ToLowerInvariant().TrimStart('.') is "jpeg" ? "jpg" : extension.ToLowerInvariant().TrimStart('.');
        var valid = Extension switch
        {
            "png" => content.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
            "jpg" => content.Length >= 4 && content[0] == 255 && content[1] == 216 && content[2] == 255,
            "svg" or "mmd" => IsText(content),
            _ => false
        };
        if (!valid) throw new ArgumentException("Use a PNG/JPEG image or UTF-8 SVG/Mermaid source matching its extension");
        Content = (byte[])content.Clone();
        Hash = Convert.ToHexStringLower(SHA256.HashData(Content.Span));
    }

    static bool IsText(byte[] bytes)
    {
        try { return !new UTF8Encoding(false, true).GetString(bytes).Contains('\0'); }
        catch (DecoderFallbackException) { return false; }
    }
}

/// <summary>The asset branch at one commit, including the requested path's bytes, if present.</summary>
public sealed record AssetBranch(string Commit, bool Owned, byte[]? Content);

/// <summary>The approved repository's Discussion lookup and optimistic, append-only asset storage.</summary>
public interface IDiscussionAssetRepository
{
    string Repository { get; }
    Task<bool> HasDiscussion(int number, CancellationToken ct);
    Task<AssetBranch?> Read(string path, CancellationToken ct);
    /// <summary>Creates an orphan branch or appends to the expected commit. False means another writer won.</summary>
    Task<bool> Append(AssetBranch? expected, string path, ReadOnlyMemory<byte> content, CancellationToken ct);
}

/// <summary>Publishes immutable assets, reusing successful uncertain writes and preserving earlier references.</summary>
public sealed class DiscussionAssets(IDiscussionAssetRepository repository)
{
    public const string Branch = "repoagent-assets";

    public async Task<string> Publish(int discussion, DiscussionAsset asset, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (discussion <= 0 || !await repository.HasDiscussion(discussion, ct))
            throw new InvalidOperationException("Discussion does not belong to the approved repository");
        var path = $"discussions/{discussion.ToString(CultureInfo.InvariantCulture)}/{asset.Hash}.{asset.Extension}";
        for (var attempt = 0; attempt <= 5; attempt++)
        {
            var head = await repository.Read(path, ct);
            if (head is { Owned: false }) throw new InvalidOperationException("The repoagent-assets branch is not owned by the asset publisher");
            if (head?.Content is { } existing)
            {
                if (!existing.AsSpan().SequenceEqual(asset.Content.Span)) throw new InvalidOperationException("Existing asset content differs from its hash path");
                return Url(head.Commit, path);
            }
            if (attempt == 5) break;
            if (!await repository.Append(head, path, asset.Content, ct)) continue;
            // Read back even after a successful write; the returned reference must name verified persisted bytes.
        }
        throw new IOException("Asset branch changed repeatedly; retry publication with the same file");
    }

    string Url(string commit, string path) => $"https://github.com/{repository.Repository}/blob/{commit}/{path}?raw=true";
}
