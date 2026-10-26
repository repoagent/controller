using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RepoAgentController;

/// <summary>Consumes only leases bound to the operator's explicitly versioned protected enrollment.</summary>
internal sealed class BrokerTokenSource : IInstallationTokenSource
{
    const int MaximumBytes = 64 * 1024;
    const string VariableName = @"\A[A-Za-z_][A-Za-z0-9_]{0,127}\z";
    // Setup v4 Agent-variable approval; its settings profiles read Agent variables (ADR 0012).
    const string SettingsPolicy = "repository-agent-settings-approval-v4";
    // Setup v3 Actions-variable policies, retired by ADR 0012.
    const string RetiredClonePolicy = "repository-actions-clone-approval-v3";
    const string RetiredSettingsPolicy = "repository-actions-settings-approval-v3";
    static readonly string[] BindingFields = ["protocolVersion", "appId", "appSlug", "installationId", "repositoryId", "repository", "permissions"];
    readonly HttpClient http;
    readonly Uri origin;
    readonly JsonDocument enrollment;
    readonly TimeProvider clock;
    readonly RuntimeProfile profile;
    bool clone => profile.ManagedClone;
    bool settings => profile.Settings;
    readonly int version;
    // Clone (v2) and repository-settings (v3) files and leases name their profile and approval policy.
    bool NamesProfile => clone || settings;
    string[] Fields => NamesProfile ? [.. BindingFields, "profile", "policyVersion"] : BindingFields;
    public long? RepositoryId => clone ? Id(enrollment.RootElement, "repositoryId") : null;
    public string? GitRepository => clone ? Text(enrollment.RootElement, "repository") : null;
    public string? GitProfile => clone ? Text(enrollment.RootElement, "profile") : null;
    readonly string deviceId;
    readonly string secret;
    public string AppSlug => enrollment.RootElement.GetProperty("appSlug").GetString()!;

    /// <param name="readEnrollment">Supplies the protected enrollment document, from <see cref="EnrollmentFile"/> or
    /// <see cref="Injected"/>; every source gets the same validation.</param>
    internal BrokerTokenSource(HttpClient http, string brokerOrigin, Func<ReadOnlyMemory<byte>> readEnrollment, string appId, string repository,
        TimeProvider clock, RuntimeProfile profile, long? expectedRepositoryId = null, long? expectedInstallationId = null)
    {
        this.http = http;
        this.clock = clock;
        this.profile = profile;
        origin = ParseOrigin(brokerOrigin);
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Broker credentials require the Linux or macOS controller runtime");
        enrollment = Parse(readEnrollment());
        try
        {
            var root = enrollment.RootElement;
            version = checked((int)Id(root, "protocolVersion"));
            // ADR 0012 retires setup v3 (Actions-variable approval). Its clone and settings files name their policy; a v3
            // polling or webhook file names none and cannot be told apart from setup v2.
            if (root.TryGetProperty("policyVersion", out var policy) && policy.ValueKind == JsonValueKind.String &&
                policy.GetString() is RetiredClonePolicy or RetiredSettingsPolicy)
                throw Invalid("this setup v3 (Actions-variable) enrollment is retired because approval and settings moved to Agent variables; revoke it and enroll afresh");
            // A repository-settings profile is explicit on both sides: frozen profiles are never widened, and a
            // repository-settings profile is never consumed as a frozen one.
            if (settings && version != 3) throw Invalid("--repository-settings requires a controller enrollment with a repository-settings profile (runtime v3); frozen profiles are not widened");
            if (!settings && version == 3) throw Invalid("a repository-settings profile requires --repository-settings");
            if (version != profile.Version) throw Invalid("explicit runtime selection differs from enrollment version");
            CheckFields(root, [.. Fields, "brokerOrigin", "githubHost", "deviceId", "deviceSecret"]);
            CheckBinding(root);
            if (Text(root, "githubHost") != "github.com") throw Invalid("only github.com is supported");
            if (ParseOrigin(Text(root, "brokerOrigin")) != origin) throw Invalid("broker origin differs from enrollment");
            if (!long.TryParse(appId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id != Id(root, "appId") ||
                !string.Equals(repository, Text(root, "repository"), StringComparison.OrdinalIgnoreCase))
                throw Invalid("CLI App/repository differs from enrollment");
            if (expectedRepositoryId is { } expected && (!clone || expected <= 0 || Id(root, "repositoryId") != expected))
                throw Invalid("CLI repository ID differs from enrollment");
            ValidateInstallation(expectedInstallationId);
            deviceId = Text(root, "deviceId");
            secret = Text(root, "deviceSecret");
            if (!Regex.IsMatch(deviceId, @"\A[A-Za-z0-9_-]{1,128}\z") || !Regex.IsMatch(secret, @"\A[A-Za-z0-9_-]{43,128}\z"))
                throw Invalid("invalid device credential format");
            Log.Protect(secret);
        }
        catch { enrollment.Dispose(); throw; }
    }

    /// <summary>Reads a protected enrollment file.</summary>
    internal static Func<ReadOnlyMemory<byte>> EnrollmentFile(string enrollmentFile) => () => ReadFile(enrollmentFile);

    static ReadOnlyMemory<byte> ReadFile(string enrollmentFile)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Broker credentials require the Linux or macOS controller runtime");
        if ((File.GetUnixFileMode(enrollmentFile) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite)) != 0)
            throw Invalid("enrollment file must have owner-only read/write permissions (0600)");
        using var file = File.OpenRead(enrollmentFile);
        if (file.Length > MaximumBytes) throw Invalid("enrollment exceeds 64 KiB");
        var bytes = new byte[MaximumBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = file.Read(bytes, length, bytes.Length - length);
            if (read == 0) break;
            length += read;
        }
        return bytes.AsMemory(0, length);
    }

    /// <summary>
    /// Removes an externally injected enrollment from this process environment and returns it, so no
    /// later gh, Git or native child can inherit it. Returns null when no variable is selected.
    /// </summary>
    internal static string? TakeInjected(string? variable)
    {
        if (variable is null || !Regex.IsMatch(variable, VariableName)) return null;
        var value = Environment.GetEnvironmentVariable(variable);
        Environment.SetEnvironmentVariable(variable, null);
        return value;
    }

    /// <summary>Validates an injected enrollment taken by <see cref="TakeInjected"/> as the same bounded document as a file.</summary>
    internal static Func<ReadOnlyMemory<byte>> Injected(string variable, string? value) => () =>
    {
        if (!Regex.IsMatch(variable, VariableName)) throw Invalid("invalid enrollment environment variable name");
        if (string.IsNullOrEmpty(value)) throw Invalid($"enrollment environment variable {variable} is missing or empty");
        if (System.Text.Encoding.UTF8.GetByteCount(value) > MaximumBytes) throw Invalid("enrollment exceeds 64 KiB");
        return System.Text.Encoding.UTF8.GetBytes(value);
    };

    public static Uri ParseOrigin(string value)
    {
        if (!Regex.IsMatch(value, @"\Ahttps://[A-Za-z0-9.-]+(?::[0-9]+)?/?\z", RegexOptions.IgnoreCase) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns ||
            uri.UserInfo.Length != 0 || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw Invalid("broker URL must be an HTTPS origin without credentials, path, query or fragment");
        return uri;
    }

    public Task Initialize(long? expectedInstallation, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ValidateInstallation(expectedInstallation);
        return Task.CompletedTask;
    }

    void ValidateInstallation(long? expectedInstallation)
    {
        if (expectedInstallation is not null && expectedInstallation != Id(enrollment.RootElement, "installationId"))
            throw Invalid("CLI installation differs from enrollment");
    }

    public async Task<InstallationLease> Acquire(CancellationToken ct)
    {
        // ResponseHeadersRead ends HttpClient.Timeout at the headers; bound body consumption too.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60), clock);
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = acquisition.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(origin, $"/v{version}/devices/{deviceId}/installation-token"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) { throw Invalid("broker transport failed"); }
        using (response)
        {
            if (!response.IsSuccessStatusCode) throw Invalid($"broker request failed (HTTP {(int)response.StatusCode})");
            if (response.Content.Headers.ContentLength > MaximumBytes) throw Invalid("response exceeds 64 KiB");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            var bytes = new byte[MaximumBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes.AsMemory(length), ct);
                if (read == 0) break;
                length += read;
            }
            using var document = Parse(bytes.AsMemory(0, length));
            var result = document.RootElement;
            CheckFields(result, [.. Fields, "token", "expiresAt"]);
            CheckBinding(result);
            var approved = enrollment.RootElement;
            foreach (var field in new[] { "appId", "installationId", "repositoryId" })
                if (Id(result, field) != Id(approved, field)) throw Invalid("lease immutable identity differs from enrollment");
            foreach (var field in new[] { "appSlug", "repository" })
                if (!string.Equals(Text(result, field), Text(approved, field), StringComparison.OrdinalIgnoreCase))
                    throw Invalid("lease App/repository differs from enrollment");
            if (NamesProfile && Text(result, "policyVersion") != Text(approved, "policyVersion"))
                throw Invalid("lease approval policy differs from enrollment");
            var token = Text(result, "token");
            var expiryText = Text(result, "expiresAt");
            if (token.Length == 0 || token.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
                !Regex.IsMatch(expiryText, @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})\z") ||
                !DateTimeOffset.TryParse(expiryText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry) ||
                expiry <= clock.GetUtcNow().AddMinutes(5) || expiry > clock.GetUtcNow().AddMinutes(65))
                throw Invalid("unusable installation token or expiry");
            return new(token, expiry);
        }
    }

    void CheckBinding(JsonElement value)
    {
        if (Id(value, "protocolVersion") != version) throw Invalid("unsupported protocol version");
        foreach (var field in new[] { "appId", "installationId", "repositoryId" }) _ = Id(value, field);
        if (!Name(Text(value, "appSlug"))) throw Invalid("invalid App slug");
        var parts = Text(value, "repository").Split('/');
        if (parts.Length != 2 || !parts.All(Name)) throw Invalid("invalid repository name");
        if (settings && (Text(value, "profile") != profile.Name ||
            Text(value, "policyVersion") != SettingsPolicy))
            throw Invalid("repository-settings policy differs");
        if (clone && !settings && (Text(value, "profile") != profile.Name ||
            Text(value, "policyVersion") is not ("repository-agent-clone-approval-v4" or "repository-agent-clone-approval-v2")))
            throw Invalid("clone policy differs");
        var permissions = value.GetProperty("permissions");
        var required = profile.Permissions();
        CheckFields(permissions, [.. required.Select(permission => permission.Key)], ["metadata"]);
        if (required.Any(permission => Text(permissions, permission.Key) != permission.Value!.GetValue<string>()) ||
            (permissions.TryGetProperty("metadata", out var metadata) && (metadata.ValueKind != JsonValueKind.String || metadata.GetString() != "read")))
            throw Invalid("permission profile differs from runtime");
    }

    static bool Name(string value) => value is not "." and not ".." && Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]+\z");
    static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
        ? property.GetString()! : throw Invalid("missing or invalid string field");
    static long Id(JsonElement value, string name) => value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var id) && id > 0
        ? id : throw Invalid("missing or invalid integer field");
    static void CheckFields(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("expected JSON object");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name) || (!required.Contains(property.Name) && !(optional?.Contains(property.Name) ?? false)))
                throw Invalid("unknown or duplicate field");
        if (required.Any(name => !names.Contains(name))) throw Invalid("missing required field");
    }
    static JsonDocument Parse(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length > MaximumBytes) throw Invalid("document exceeds 64 KiB");
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw Invalid("malformed JSON document"); }
    }
    static InvalidOperationException Invalid(string reason) => new($"Broker authentication failed: {reason}");
    public void Dispose() => enrollment.Dispose();
}
