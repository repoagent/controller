using System.Text.Json.Nodes;
using RepoAgentController;

return await BridgeCommand.Run(args);

namespace RepoAgentController
{
    static class Log
    {
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> secrets = new();
        public static void Protect(string secret) => secrets.TryAdd(secret, 0);
        public static void Write(string kind, string message)
        {
            foreach (var secret in secrets.Keys) message = message.Replace(secret, "<redacted>", StringComparison.Ordinal);
            Console.WriteLine($"{DateTimeOffset.UtcNow:O} {kind} {message}");
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
