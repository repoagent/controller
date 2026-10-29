using System.Text.Json.Nodes;
using RepoAgentController;

return await BridgeCommand.Run(args);

namespace RepoAgentController
{
    static class Log
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> secrets = new();
        // Scoped to one controller lifetime's async flow, so concurrent in-process lifetimes stay separate.
        static readonly AsyncLocal<Action<LogEntry>?> observer = new();
        // Also scoped to a lifetime's flow: its entries carry that lifetime's time.
        static readonly AsyncLocal<TimeProvider?> time = new();
        public static void Protect(string secret) => secrets.TryAdd(secret, 0);
        internal static int ProtectedLength => secrets.Keys.Select(secret => secret.Length).DefaultIfEmpty().Max();
        /// <summary>Removes every known secret, as every log entry does.</summary>
        public static string Redact(string message)
        {
            foreach (var secret in secrets.Keys) message = message.Replace(secret, "<redacted>", StringComparison.Ordinal);
            return message;
        }

        public static void Write(string kind, string message)
        {
            message = Redact(message);
            // In UTC whatever offset a clock reports, as every line has been.
            var entry = new LogEntry((time.Value ?? TimeProvider.System).GetUtcNow().ToUniversalTime(), kind, message);
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

        /// <summary>Stamps this flow's entries with <paramref name="clock"/>'s time until disposed; otherwise they carry the
        /// system's.</summary>
        public static IDisposable Clock(TimeProvider clock)
        {
            var previous = time.Value;
            time.Value = clock;
            return new Restore(() => time.Value = previous);
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
