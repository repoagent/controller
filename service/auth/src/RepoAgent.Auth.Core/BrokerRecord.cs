namespace RepoAgent.Auth;

// A durable broker record. Its Map binds every stored property to one field, under the property's stored name and
// type, and reading and writing use that same binding. A Replace writes the whole row, so a property the record did not
// know would be deleted: a row is read only when the record knows every property in it, with the stored type.
public abstract class BrokerRecord
{
    protected abstract void Map(RecordMap map);

    // Throws BrokerRecordUnreadable for a property the record does not know or a value of another type.
    public static T Read<T>(IReadOnlyDictionary<string, object> row) where T : BrokerRecord, new()
    {
        var record = new T();
        var map = new RecordMap(row);
        record.Map(map);
        map.RequireEveryPropertyMapped();
        return record;
    }

    // The stored properties: every field that has a value, under its stored name. Absent fields stay absent.
    public IReadOnlyDictionary<string, object> ToRow()
    {
        var map = new RecordMap(null);
        Map(map);
        return map.Written;
    }
}

// Binds stored properties to record fields. Reads when it has a stored row, otherwise writes. The value types are the
// Table EDM types the broker stores: String, Int64, DateTime (as DateTimeOffset) and Boolean.
public sealed class RecordMap
{
    private readonly IReadOnlyDictionary<string, object>? stored;
    private readonly HashSet<string> names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, object> written = new(StringComparer.Ordinal);

    internal RecordMap(IReadOnlyDictionary<string, object>? stored) => this.stored = stored;

    internal IReadOnlyDictionary<string, object> Written => written;

    public void Text(string name, ref string? field)
    {
        Claim(name);
        if (stored is null)
        {
            if (field is not null) written.Add(name, field);
        }
        else field = stored.TryGetValue(name, out var value) ? value as string ?? throw Unreadable(name) : null;
    }

    public void Number(string name, ref long? field) => Value(name, ref field);
    public void Time(string name, ref DateTimeOffset? field) => Value(name, ref field);
    public void Flag(string name, ref bool? field) => Value(name, ref field);

    private void Value<TValue>(string name, ref TValue? field) where TValue : struct
    {
        Claim(name);
        if (stored is null)
        {
            if (field is { } value) written.Add(name, value);
        }
        else field = stored.TryGetValue(name, out var value) ? value is TValue typed ? typed : throw Unreadable(name) : null;
    }

    private void Claim(string name)
    {
        if (!names.Add(name)) throw new InvalidOperationException($"The stored property {name} is mapped twice.");
    }

    internal void RequireEveryPropertyMapped()
    {
        foreach (var name in stored!.Keys)
            if (!names.Contains(name)) throw new BrokerRecordUnreadable($"The stored row has a property its record does not know: {name}.");
    }

    private static BrokerRecordUnreadable Unreadable(string name) => new($"The stored property {name} has another type.");
}
