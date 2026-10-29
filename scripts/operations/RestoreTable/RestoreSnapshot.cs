using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace RepoAgent.RestoreTable;

public sealed record RestoreRun
{
    private RestoreRun(string id) => Id = id;
    public string Id { get; }
    public const string SourceTable = "brokerrehearsal";
    public const string TargetTable = "brokerrestore";
    public string Account => "ra230" + Id.Replace("-", "", StringComparison.Ordinal);
    public Uri Endpoint => new($"https://{Account}.table.core.windows.net/");

    public static RestoreRun Parse(string value)
    {
        if (!Regex.IsMatch(value, "\\A[0-9]{8}-[0-9a-f]{6}\\z", RegexOptions.CultureInvariant)
            || !DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidDataException("Invalid rehearsal run identifier.");
        return new(value);
    }

    public static Uri LocalEndpoint(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http"
            || endpoint.Host != "127.0.0.1" || endpoint.IsDefaultPort || endpoint.Port < 1024
            || endpoint.AbsolutePath != "/devstoreaccount1" || endpoint.Query.Length != 0
            || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0)
            throw new InvalidDataException("Local mode requires an explicit IPv4 loopback Azurite Table endpoint.");
        return endpoint;
    }

    public static bool AllowsRequest(Uri endpoint, string table, Uri request, string method)
    {
        if (table is not (SourceTable or TargetTable) || request.UserInfo.Length != 0 || request.Fragment.Length != 0
            || !string.Equals(endpoint.GetLeftPart(UriPartial.Authority), request.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal))
            return false;
        var path = request.AbsolutePath;
        var prefix = endpoint.AbsolutePath.TrimEnd('/');
        if (path == prefix + "/Tables") return method == "POST";
        var target = prefix + "/" + table;
        return method is "GET" or "POST" && (path == target || path.StartsWith(target + "(", StringComparison.Ordinal));
    }
}

public sealed record EdmProperty(string Type, string Value)
{
    public static EdmProperty Encode(object value) => value switch
    {
        string text => new("String", text),
        bool flag => new("Boolean", flag ? "true" : "false"),
        int number => new("Int32", number.ToString(CultureInfo.InvariantCulture)),
        long number => new("Int64", number.ToString(CultureInfo.InvariantCulture)),
        double number when double.IsFinite(number) => new("Double", number.ToString("R", CultureInfo.InvariantCulture)),
        Guid guid => new("Guid", guid.ToString("D")),
        byte[] bytes => new("Binary", Convert.ToBase64String(bytes)),
        DateTimeOffset time => new("DateTime", time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)),
        DateTime time when time.Kind == DateTimeKind.Utc => Encode(new DateTimeOffset(time)),
        _ => throw new InvalidDataException("Unsupported durable property type.")
    };

    public object Decode()
    {
        if (Value is null) throw new InvalidDataException("Missing durable value.");
        object decoded = Type switch
        {
            "String" => Value,
            "Boolean" => bool.Parse(Value),
            "Int32" => int.Parse(Value, CultureInfo.InvariantCulture),
            "Int64" => long.Parse(Value, CultureInfo.InvariantCulture),
            "Double" => double.Parse(Value, CultureInfo.InvariantCulture),
            "Guid" => Guid.ParseExact(Value, "D"),
            "Binary" => Convert.FromBase64String(Value),
            "DateTime" => DateTimeOffset.ParseExact(Value, "O", CultureInfo.InvariantCulture, DateTimeStyles.None),
            _ => throw new InvalidDataException("Unsupported durable property type.")
        };
        if (Encode(decoded) != this) throw new InvalidDataException("Noncanonical durable value.");
        return decoded;
    }
}

public sealed record SnapshotRow(string PartitionKey, string RowKey, SortedDictionary<string, EdmProperty> Properties)
{
    public static SnapshotRow From(string partition, string key, IReadOnlyDictionary<string, object> properties)
        => new(partition, key, new SortedDictionary<string, EdmProperty>(
            properties.ToDictionary(p => p.Key, p => EdmProperty.Encode(p.Value), StringComparer.Ordinal), StringComparer.Ordinal));

    public void Validate()
    {
        foreach (var key in new[] { PartitionKey, RowKey })
            if (string.IsNullOrEmpty(key) || key.Length > 1024 || key.Any(c => char.IsControl(c) || c is '/' or '\\' or '#' or '?'))
                throw new InvalidDataException("Invalid durable row key.");
        if (Properties is null || Properties.Count > 124) throw new InvalidDataException("Invalid durable property count.");
        foreach (var (name, property) in Properties)
            if (string.IsNullOrEmpty(name) || name.Length > 255 || IsSystem(name) || property is null)
                throw new InvalidDataException("Invalid durable property name.");
        var decoded = Properties.ToDictionary(p => p.Key, p => p.Value.Decode(), StringComparer.Ordinal);
        if (JsonSerializer.SerializeToUtf8Bytes(decoded).Length > 64 * 1024)
            throw new InvalidDataException("Durable row exceeds its size budget.");
    }

    public static bool IsSystem(string name) => name is "PartitionKey" or "RowKey" or "Timestamp" or "odata.etag";
}

public sealed record SnapshotDocument(int Schema, string RunId, string SourceEndpoint, string SourceTable,
    DateTimeOffset CapturedAt, int Pages, SnapshotRow[] Rows, string ContentSha256)
{
    public const int MaxRows = 18948;
    public const int MaxBytes = 64 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static SnapshotDocument Create(RestoreRun run, Uri endpoint, IEnumerable<SnapshotRow> rows, int pages)
    {
        var ordered = rows.OrderBy(r => r.PartitionKey, StringComparer.Ordinal).ThenBy(r => r.RowKey, StringComparer.Ordinal).ToArray();
        return new(1, run.Id, endpoint.AbsoluteUri, RestoreRun.SourceTable, DateTimeOffset.UtcNow, pages, ordered, Digest(ordered));
    }

    public static string Digest(IEnumerable<SnapshotRow> rows) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(rows.Select(row => row with
        { Properties = new SortedDictionary<string, EdmProperty>(row.Properties, StringComparer.Ordinal) }), Options)));

    public byte[] Write()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(this, Options);
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Snapshot exceeds its size budget.");
        return bytes;
    }

    public static SnapshotDocument Read(byte[] bytes, RestoreRun run, Uri endpoint)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Snapshot exceeds its size budget.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicates(document.RootElement);
        var snapshot = JsonSerializer.Deserialize<SnapshotDocument>(bytes, Options) ?? throw new InvalidDataException("Missing snapshot.");
        snapshot.Validate(run, endpoint);
        return snapshot;
    }

    public void Validate(RestoreRun run, Uri endpoint)
    {
        if (Schema != 1 || RunId != run.Id || SourceEndpoint != endpoint.AbsoluteUri || SourceTable != RestoreRun.SourceTable
            || Pages is < 1 or > 1024 || Rows is null || Rows.Length > MaxRows || CapturedAt == default)
            throw new InvalidDataException("Snapshot binding is incomplete or differs from this run.");
        string? previous = null;
        foreach (var row in Rows)
        {
            row.Validate();
            var key = row.PartitionKey + '\0' + row.RowKey;
            if (previous is not null && string.CompareOrdinal(previous, key) >= 0)
                throw new InvalidDataException("Snapshot contains duplicate or unordered rows.");
            previous = key;
        }
        if (ContentSha256 != Digest(Rows)) throw new InvalidDataException("Snapshot integrity check failed.");
    }

    internal static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate snapshot property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
}
