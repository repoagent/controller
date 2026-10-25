namespace RepoAgent.Auth;

// The broker's durable records, read and written with optimistic concurrency. The adapter translates the store's
// rows, versions and failures; what a record means, and what to do about a conflict, is the core's decision.
public interface IBrokerState
{
    // Null when the record does not exist. Throws BrokerStateUnavailable when the store does not answer, and
    // BrokerRecordUnreadable when the stored row is not exactly a record of this type.
    Task<Versioned<T>?> ReadAsync<T>(StateKey<T> key, CancellationToken ct) where T : BrokerRecord, new();

    // Applies every write or none, and returns each write's new version (null for a delete). StateConflict means a
    // precondition failed and nothing was written. Any other failure leaves the outcome unknown: the transaction may
    // have committed, so callers re-read and never compensate.
    Task<IReadOnlyList<StateVersion?>> CommitAsync(StateTransaction transaction, CancellationToken ct);

    // One page of at most 32 records of a range, in ordinal name order, whose names sort after `after`. A page may be
    // empty and still carry a continuation; only a null continuation ends the range. Throws BrokerStateUnavailable when
    // the store does not answer. The answer is not checked against the query: each record carries the scope and name
    // the store answered, for the caller to check. Each listed record is read on its own, so a row no record can read
    // fails only the caller that reads it.
    Task<StatePage<T>> ListAsync<T>(StateRange<T> range, string after, string? continuation, CancellationToken ct)
        where T : BrokerRecord, new();
}

// Where one record lives: a scope, which a transaction never spans, and the record's name within it. Factories on the
// record types build keys from the stored partition and row keys, which are part of the persisted format.
public abstract class StateKey(string scope, string name)
{
    public string Scope { get; } = scope;
    public string Name { get; } = name;
    public override string ToString() => Scope + "/" + Name;
}

public sealed class StateKey<T>(string scope, string name) : StateKey(scope, name) where T : BrokerRecord, new();

// The records of one scope whose names sort ordinally from `From` (inclusive) to `To` (exclusive).
public sealed class StateRange<T>(string scope, string from, string to) where T : BrokerRecord, new()
{
    public string Scope { get; } = scope;
    public string From { get; } = from;
    public string To { get; } = to;
}

public sealed record StatePage<T>(IReadOnlyList<ListedRecord<T>> Records, string? Continuation) where T : BrokerRecord, new();

// A record a listing returned, with the version it was listed at. Read throws BrokerRecordUnreadable when the stored
// row is not exactly a record of this type.
public sealed class ListedRecord<T>(StateKey<T> key, IReadOnlyDictionary<string, object> row, StateVersion version)
    where T : BrokerRecord, new()
{
    public StateKey<T> Key { get; } = key;
    public Versioned<T> Read() => new(BrokerRecord.Read<T>(row), version);
}

public sealed record Versioned<T>(T Value, StateVersion Version) where T : BrokerRecord;

// The version a record had when it was read or written. Every write issues a new one, even when nothing changed, so a
// write conditioned on a version fences every writer that came between. Opaque to the core.
public sealed record StateVersion(string Token);

public enum StateWriteKind { Add, Replace, Delete }

// One write of a transaction. The row is the record as it was when the write was added.
public sealed record StateWrite(StateWriteKind Kind, StateKey Key, IReadOnlyDictionary<string, object>? Row, StateVersion? Version);

// Writes that commit together or not at all: at most 32 distinct records of one scope, as a Table entity group
// transaction allows. Add requires that the record is absent; Replace and Delete require the version that was read.
public sealed class StateTransaction
{
    public const int MaxWrites = 32;
    private readonly List<StateWrite> writes = [];

    public IReadOnlyList<StateWrite> Writes => writes;

    public StateTransaction Add<T>(StateKey<T> key, T record) where T : BrokerRecord, new() =>
        With(new(StateWriteKind.Add, key, record.ToRow(), null));

    public StateTransaction Replace<T>(StateKey<T> key, T record, StateVersion version) where T : BrokerRecord, new() =>
        With(new(StateWriteKind.Replace, key, record.ToRow(), version));

    public StateTransaction Delete<T>(StateKey<T> key, StateVersion version) where T : BrokerRecord, new() =>
        With(new(StateWriteKind.Delete, key, null, version));

    private StateTransaction With(StateWrite write)
    {
        if (writes.Count == MaxWrites || writes.Count > 0 && writes[0].Key.Scope != write.Key.Scope
            || writes.Any(existing => existing.Key.Name == write.Key.Name))
            throw new InvalidOperationException("A state transaction writes at most 32 distinct records of one scope.");
        writes.Add(write);
        return this;
    }
}

// The store did not answer, or answered in a way that proves nothing. After a commit, the outcome is unknown.
public sealed class BrokerStateUnavailable(string message, Exception? cause = null) : Exception(message, cause);

// A write's precondition failed: another writer got there first. Nothing in the transaction was written.
public sealed class StateConflict() : Exception("Another writer changed the records first; nothing was written.");

// The stored row is not exactly a record of the expected type, so the core must not act on it or rewrite it.
public sealed class BrokerRecordUnreadable(string message) : Exception(message);
