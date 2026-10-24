using System.Text.Json.Nodes;
using RepoAgentController;

return await BridgeCommand.Run(args);

namespace RepoAgentController
{
    /// <summary>One redacted controller event, written to stdout and to any observer of this controller lifetime.</summary>
    readonly record struct LogEntry(DateTimeOffset At, string Kind, string Message)
    {
        public string Line => $"{At:O} {Kind} {Message}";
    }

    static class Log
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> secrets = new();
        // Scoped to one controller lifetime's async flow, so concurrent in-process lifetimes stay separate.
        static readonly AsyncLocal<Action<LogEntry>?> observer = new();
        public static void Protect(string secret) => secrets.TryAdd(secret, 0);
        /// <summary>Removes every known secret, as every log entry does.</summary>
        public static string Redact(string message)
        {
            foreach (var secret in secrets.Keys) message = message.Replace(secret, "<redacted>", StringComparison.Ordinal);
            return message;
        }

        public static void Write(string kind, string message)
        {
            message = Redact(message);
            var entry = new LogEntry(DateTimeOffset.UtcNow, kind, message);
            Console.WriteLine(entry.Line);
            observer.Value?.Invoke(entry);
        }

        /// <summary>Also delivers this flow's redacted entries to <paramref name="receive"/> until disposed.</summary>
        public static IDisposable Observe(Action<LogEntry> receive)
        {
            var previous = observer.Value;
            observer.Value = receive;
            return new Restore(() => observer.Value = previous);
        }

        sealed class Restore(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }
    }

    /// <summary>The <see cref="IControllerLog"/> adapter: writes through <see cref="Log.Write"/>, so kinds, messages, redaction
    /// and observers are unchanged.</summary>
    sealed class ControllerLog : IControllerLog
    {
        public void Write(string kind, string message) => Log.Write(kind, message);
    }
    static class RetryDelay
    {
        /// <summary>Doubles from <paramref name="first"/> with each 1-based attempt, up to <paramref name="longest"/>.</summary>
        public static TimeSpan Doubling(TimeSpan first, TimeSpan longest, int attempt) =>
            TimeSpan.FromTicks(Math.Min(longest.Ticks, first.Ticks << Math.Min(attempt - 1, 16)));
    }

    /// <summary>
    /// A background loop of passes. After each pass it sleeps for the delay the pass returns; when the pass allows it, a signal
    /// ends that sleep early. A signal raised during a pass ends the following sleep at once.
    /// </summary>
    sealed class WakeLoop(TimeProvider clock)
    {
        readonly Lock sync = new();
        TaskCompletionSource wake = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Signal() { lock (sync) wake.TrySetResult(); }

        /// <summary>Runs passes until <paramref name="lifetime"/> is cancelled, then disposes it.</summary>
        public async Task Run(CancellationTokenSource lifetime, Func<CancellationToken, Task<(TimeSpan Sleep, bool Wakeable)>> pass)
        {
            var ct = lifetime.Token;
            try
            {
                while (true)
                {
                    Task signal;
                    lock (sync)
                    {
                        if (wake.Task.IsCompleted) wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        signal = wake.Task;
                    }
                    var (sleep, wakeable) = await pass(ct);
                    using var timer = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var delay = Task.Delay(sleep, clock, timer.Token);
                    await (wakeable ? Task.WhenAny(signal, delay) : delay);
                    await timer.CancelAsync();
                    ct.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            finally { lifetime.Dispose(); }
        }
    }

    static class Json
    {
        public static string Text(this JsonNode node, string name) => node[name]?.GetValue<string>() ?? "";
        /// <summary>The string a node holds, or null when it is absent or holds another type or shape. External input reads
        /// leniently through this; <see cref="Text"/> is for controller-written documents and throws on a wrong type.</summary>
        public static string? OptionalText(this JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        public static JsonNode Copy(this JsonNode node) => node.DeepClone();
        public static JsonArray Array(IEnumerable<JsonNode> nodes) => new(nodes.Select(n => n.Copy()).ToArray());
        public static JsonObject Object(params (string Name, JsonNode? Value)[] values) =>
            new(values.Select(value => new KeyValuePair<string, JsonNode?>(value.Name, value.Value)));
        public static string Encode(JsonNode value) => value.ToJsonString();
    }
}
