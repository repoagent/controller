using System.Text.Json;
using System.Text.Json.Nodes;

namespace RepoAgentController;

sealed partial class Codex
{
    sealed class UsageUnsupportedException : IOException;

    // Codex 0.165.0 account/rateLimits/read. Only this owned process supplies account observations.
    public async Task<AgentUsageObservation> ReadUsage(CancellationToken ct)
    {
        try
        {
            var response = await Request("account/rateLimits/read", new JsonObject(), ct);
            if (response is not JsonObject root) throw new JsonException();
            var buckets = new List<AgentUsageBucket>();
            if (root["rateLimitsByLimitId"] is not (null or JsonObject)) throw new JsonException();
            if (root["rateLimitsByLimitId"] is JsonObject { Count: > 0 } multiple)
            {
                if (multiple.Count > 100) throw new JsonException();
                foreach (var (id, value) in multiple.OrderBy(pair => pair.Key, StringComparer.Ordinal)) buckets.Add(Bucket(id, value));
            }
            else buckets.Add(Bucket("account", root["rateLimits"]));
            return new(AgentUsageAvailability.Available, buckets)
            {
                AccountKey = OptionalText(root, "accountId"),
                OrdinaryUsageAllowed = OptionalBoolean(root, "ordinaryUsageAllowed")
            };
        }
        catch (UsageUnsupportedException)
        { return new(AgentUsageAvailability.Unsupported, []); }
        catch (Exception error) when (error is AgentRequestRejected or JsonException or InvalidOperationException or ArgumentException or OverflowException)
        { return new(AgentUsageAvailability.Unavailable, []); }
    }

    static AgentUsageBucket Bucket(string fallback, JsonNode? node)
    {
        if (node is not JsonObject value) throw new JsonException();
        var windows = new List<AgentUsageWindow>();
        Window("primary", value["primary"], windows);
        Window("secondary", value["secondary"], windows);
        if (value["individualLimit"] is JsonObject individual)
            windows.Add(new("individual spend control", 100 - Percentage(individual, "remainingPercent"), null, Reset(individual)) { IsSpendControl = true });
        else if (value["individualLimit"] is not null) throw new JsonException();
        if (value["credits"] is not (null or JsonObject)) throw new JsonException();
        var credits = value["credits"] as JsonObject;
        return new(OptionalText(value, "limitId") ?? fallback, OptionalText(value, "limitName"),
            OptionalText(value, "normalModelSlug"), OptionalText(value, "planType"), windows)
        {
            AdditionalUsageAvailable = credits is not null && (OptionalBoolean(credits, "hasCredits") == true || OptionalBoolean(credits, "unlimited") == true),
            SpendControlReached = OptionalBoolean(value, "spendControlReached"),
            ReachedType = OptionalText(value, "rateLimitReachedType")
        };
    }

    static void Window(string label, JsonNode? node, List<AgentUsageWindow> windows)
    {
        if (node is null) return;
        if (node is not JsonObject value) throw new JsonException();
        long? minutes = null;
        if (value["windowDurationMins"] is { } duration)
        {
            if (duration is not JsonValue number || !number.TryGetValue<long>(out var durationMinutes) || durationMinutes <= 0) throw new JsonException();
            minutes = durationMinutes;
        }
        windows.Add(new(label, Percentage(value, "usedPercent"), minutes, Reset(value)));
    }

    static decimal Percentage(JsonObject value, string field) => value[field] is JsonValue number &&
        number.TryGetValue<decimal>(out var percent) && percent >= 0 && (field != "remainingPercent" || percent <= 100) ? percent : throw new JsonException();

    static DateTimeOffset? Reset(JsonObject value) => value["resetsAt"] is null ? null :
        value["resetsAt"] is JsonValue number && number.TryGetValue<long>(out var reset) ? DateTimeOffset.FromUnixTimeSeconds(reset) : throw new JsonException();

    static string? OptionalText(JsonObject value, string field) => value[field] is null ? null :
        value[field] is JsonValue text && text.TryGetValue<string>(out var result) ? result : throw new JsonException();

    static bool? OptionalBoolean(JsonObject value, string field) => value[field] is null ? null :
        value[field] is JsonValue boolean && boolean.TryGetValue<bool>(out var result) ? result : throw new JsonException();
}
