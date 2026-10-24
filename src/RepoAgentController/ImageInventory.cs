using System.Security.Cryptography;

namespace RepoAgentController;

/// <summary>The baseline image inventory this controller was started with (--image-inventory). Its digest identifies the
/// controller's image among the published baseline releases.</summary>
static class ImageInventory
{
    /// <summary>The SHA-256 of the inventory at <paramref name="path"/>, read at every call; null without one.</summary>
    public static Func<CancellationToken, Task<string?>> Sha256(string? path) => async ct =>
        path is null ? null : Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path, ct)));
}
