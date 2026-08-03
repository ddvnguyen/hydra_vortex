using System.Text.Json;

namespace Tests.AgentWorkload;

/// <summary>
/// Small helpers for tolerant JSON value extraction shared by the CLI drivers.
/// The pi / opencode NDJSON event streams emit numeric counters that are
/// occasionally missing or typed differently; callers must not crash on that.
/// </summary>
internal static class JsonElementExtensions
{
    /// <summary>
    /// Returns the numeric value of <paramref name="element"/> as an int,
    /// or 0 when it is not a JSON number.
    /// </summary>
    public static int GetInt32OrDefault(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number ? element.GetInt32() : 0;
}
