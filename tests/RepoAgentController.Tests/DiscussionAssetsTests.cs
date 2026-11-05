using RepoAgentController;
using Xunit;

namespace RepoAgentController.Tests;

public sealed class DiscussionAssetsTests
{
    static DiscussionAsset Image(byte value = 0) => new("png", [137, 80, 78, 71, 13, 10, 26, 10, value]);

    [Fact]
    public async Task Publication_reuses_retries_and_preserves_previous_images_when_other_discussions_write()
    {
        var repository = new AssetRepository { ConflictOnce = true };
        var publisher = new DiscussionAssets(repository);
        var first = await publisher.Publish(47, Image(), TestContext.Current.CancellationToken);
        Assert.Contains("/blob/commit-1/discussions/47/", first);
        Assert.EndsWith(".png?raw=true", first);
        Assert.Equal(first, await publisher.Publish(47, Image(), TestContext.Current.CancellationToken));
        await publisher.Publish(48, Image(1), TestContext.Current.CancellationToken);
        Assert.Equal(2, repository.Files.Count);
        Assert.Equal(2, repository.Writes);
        Assert.True(repository.OrphanCreated);
    }

    [Fact]
    public async Task Lost_write_response_is_recovered_without_republishing_or_overwriting_bytes()
    {
        var repository = new AssetRepository { LoseResponse = true };
        var publisher = new DiscussionAssets(repository);
        await Assert.ThrowsAsync<IOException>(() => publisher.Publish(47, Image(), TestContext.Current.CancellationToken));
        var url = await publisher.Publish(47, Image(), TestContext.Current.CancellationToken);
        Assert.Contains("/blob/commit-1/", url);
        Assert.Equal(1, repository.Writes);
        repository.Files[Assert.Single(repository.Files).Key] = [1, 2, 3];
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.Publish(47, Image(), TestContext.Current.CancellationToken));
        Assert.Equal(1, repository.Writes);
    }

    [Fact]
    public async Task Publication_verifies_the_last_allowed_write_and_bounds_conflicting_attempts()
    {
        var repository = new AssetRepository { Conflicts = 4 };
        await new DiscussionAssets(repository).Publish(47, Image(), TestContext.Current.CancellationToken);
        Assert.Equal(1, repository.Writes);
        var blocked = new AssetRepository { Conflicts = 10 };
        await Assert.ThrowsAsync<IOException>(() => new DiscussionAssets(blocked).Publish(47, Image(), TestContext.Current.CancellationToken));
        Assert.Equal(5, blocked.Conflicts);
    }

    [Fact]
    public async Task Foreign_branch_and_foreign_discussion_are_refused_before_any_write()
    {
        var repository = new AssetRepository { Exists = false };
        var publisher = new DiscussionAssets(repository);
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.Publish(47, Image(), TestContext.Current.CancellationToken));
        repository.Exists = true;
        repository.Owned = false;
        repository.Commit = "foreign";
        await Assert.ThrowsAsync<InvalidOperationException>(() => publisher.Publish(47, Image(), TestContext.Current.CancellationToken));
        Assert.Equal(0, repository.Writes);
    }

    [Theory]
    [InlineData("png")]
    [InlineData("jpg")]
    [InlineData("html")]
    public void Mislabeled_or_unsupported_files_are_rejected(string extension) =>
        Assert.Throws<ArgumentException>(() => new DiscussionAsset(extension, [1, 2, 3]));

    sealed class AssetRepository : IDiscussionAssetRepository
    {
        public string Repository => "owner/project";
        public bool Exists = true, Owned = true, ConflictOnce, OrphanCreated, LoseResponse;
        public string? Commit;
        public int Writes, Conflicts;
        public Dictionary<string, byte[]> Files { get; } = [];
        public Task<bool> HasDiscussion(int number, CancellationToken ct) => Task.FromResult(Exists);
        public Task<AssetBranch?> Read(string path, CancellationToken ct) =>
            Task.FromResult(Commit is null ? null : new AssetBranch(Commit, Owned, Files.GetValueOrDefault(path)));
        public Task<bool> Append(AssetBranch? expected, string path, ReadOnlyMemory<byte> content, CancellationToken ct)
        {
            if (ConflictOnce) { ConflictOnce = false; return Task.FromResult(false); }
            if (Conflicts > 0) { Conflicts--; return Task.FromResult(false); }
            OrphanCreated |= expected is null;
            Files.Add(path, content.ToArray());
            Commit = "commit-" + ++Writes;
            if (LoseResponse) { LoseResponse = false; throw new IOException("Lost asset write response"); }
            return Task.FromResult(true);
        }
    }
}
