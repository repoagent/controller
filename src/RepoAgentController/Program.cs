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
        public static void Write(string kind, string message)
        {
            foreach (var secret in secrets.Keys) message = message.Replace(secret, "<redacted>", StringComparison.Ordinal);
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
    static class Json
    {
        public static string Text(this JsonNode node, string name) => node[name]?.GetValue<string>() ?? "";
        public static JsonNode Copy(this JsonNode node) => node.DeepClone();
        public static JsonArray Array(IEnumerable<JsonNode> nodes) => new(nodes.Select(n => n.Copy()).ToArray());
        public static JsonObject Object(params (string Name, JsonNode? Value)[] values) =>
            new(values.Select(value => new KeyValuePair<string, JsonNode?>(value.Name, value.Value)));
        public static string Encode(JsonNode value) => value.ToJsonString();
    }
}
