using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Shared assertion helper for the protocol-v2 streaming <c>runTests</c> shape
/// (see #1641 / protocol-v2.schema.json) — splits a line sequence returned by
/// <see cref="CliServer.SendRequestStreamingAsync"/> into its per-test
/// <c>{"type":"test"}</c> events and the single terminal
/// <c>{"type":"summary"}</c> line.
/// </summary>
internal static class ProtocolV2Streaming
{
    public static (List<JsonElement> TestEvents, JsonElement Summary) Split(IReadOnlyList<string> lines)
    {
        var events = new List<JsonElement>();
        JsonElement? summary = null;
        foreach (var line in lines)
        {
            var el = JsonSerializer.Deserialize<JsonElement>(line);
            var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "summary") summary = el;
            else if (type == "test") events.Add(el);
        }
        Assert.True(summary.HasValue, $"no summary line in: {string.Join(" | ", lines)}");
        return (events, summary!.Value);
    }
}
