using System.Text.Json;
using System.Text.Json.Serialization;

namespace RepoAgent.RestoreTable;

public sealed class PrivateArtifacts
{
    private readonly string directory;
    private static readonly JsonSerializerOptions JsonOptions = new()
    { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
        | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    public PrivateArtifacts(string path)
    {
        if (OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(path) || !Directory.Exists(path))
            throw new InvalidDataException("Use an existing private native Linux/WSL directory outside Git.");
        directory = Path.GetFullPath(path);
        for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            if (current.LinkTarget is not null || (current.Attributes & FileAttributes.ReparsePoint) != 0
                || File.Exists(Path.Combine(current.FullName, ".git")) || Directory.Exists(Path.Combine(current.FullName, ".git")))
                throw new InvalidDataException("Artifact directory crosses a link or is inside Git.");
        var mode = File.GetUnixFileMode(directory);
        const UnixFileMode owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if ((mode & Others) != 0 || (mode & owner) != owner)
            throw new InvalidDataException("Artifact directory must be private to its owner.");
    }

    public void RequireAbsent(string name)
    {
        var path = Path.Combine(directory, name);
        if (Path.Exists(path) || new FileInfo(path).LinkTarget is not null)
            throw new InvalidDataException("Artifact already exists; never overwrite operator state.");
    }

    public byte[] Read(string name)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var file = new FileInfo(Path.Combine(directory, name));
        if (file.LinkTarget is not null || (file.Attributes & FileAttributes.ReparsePoint) != 0
            || file.Length > SnapshotDocument.MaxBytes || (File.GetUnixFileMode(file.FullName) & Others) != 0)
            throw new InvalidDataException("Artifact is not a bounded private regular file.");
        using var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        int read;
        while ((read = input.Read(buffer)) != 0)
        {
            if (output.Length + read > SnapshotDocument.MaxBytes) throw new InvalidDataException("Artifact byte budget exceeded.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    public void Write(string name, byte[] bytes)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var file = new FileStream(Path.Combine(directory, name), new FileStreamOptions
        { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    public void CheckFence(RestoreRun run, Uri endpoint)
    {
        var bytes = Read("fence.json");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        SnapshotDocument.RejectDuplicates(document.RootElement);
        var fence = JsonSerializer.Deserialize<OfflineFence>(bytes, JsonOptions);
        if (fence is null || fence.Schema != 1 || fence.RunId != run.Id || fence.Endpoint != endpoint.AbsoluteUri
            || !fence.WritersStopped || !fence.SigningFenced || !fence.CapabilitiesSettled || fence.ObservedAt == default
            || fence.EvidenceReferences is not { Length: > 0 and <= 16 } || fence.EvidenceReferences.Any(string.IsNullOrWhiteSpace))
            throw new InvalidDataException("Complete run-bound operator fence attestation is required.");
    }
}

public sealed record OfflineFence(int Schema, string RunId, string Endpoint, bool WritersStopped,
    bool SigningFenced, bool CapabilitiesSettled, DateTimeOffset ObservedAt, string[] EvidenceReferences);
