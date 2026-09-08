// MetadataDifferenceAllowlist — the declared differences between BC's own metadata emitter
// and the runner's SymbolReference-derived derivation, and the ONLY thing that keeps a
// difference from failing the run.
//
// The contract (issue #3533):
//   * every declared difference carries a reason and, where it is a defect rather than a
//     permanent limit, the issue tracking it;
//   * an entry that stops matching anything is an ERROR, not a tidy-up — that is how a
//     landed fix forces the allowlist to shrink instead of leaving stale cover behind;
//   * an entry may cap how many differences it covers, so a defect getting WORSE fails even
//     though its shape is declared.
//
// Drift is loud in both directions, the same way tests/expectations/ already works for
// out-of-scope corpus tests (docs/expectations.md).

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlRunner.Metadata;

/// <summary>One declared difference. Matching is on <see cref="Member"/>, optionally narrowed.</summary>
public sealed class MetadataAllowlistEntry
{
    /// <summary><c>DeclaringType.Member</c>, e.g. <c>MetaField.Editable</c>.</summary>
    [JsonPropertyName("member")] public string Member { get; init; } = "";

    /// <summary>Why this difference is tolerated. Free text; a placeholder is refused.</summary>
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";

    /// <summary>The issue tracking the fix, for a difference that is a defect.</summary>
    [JsonPropertyName("issue")] public int? Issue { get; init; }

    /// <summary>
    /// True when the difference is a permanent limit of the symbol file rather than a defect —
    /// the "cannot express" set. Such an entry needs no issue.
    /// </summary>
    [JsonPropertyName("cannotExpress")] public bool CannotExpress { get; init; }

    /// <summary>Only cover differences on this object, e.g. <c>Table 2000000120</c>.</summary>
    [JsonPropertyName("objectKey")] public string? ObjectKey { get; init; }

    /// <summary>Only cover differences whose path starts with this.</summary>
    [JsonPropertyName("pathPrefix")] public string? PathPrefix { get; init; }

    /// <summary>Fail if more than this many differences match. Null means uncapped.</summary>
    [JsonPropertyName("maxOccurrences")] public int? MaxOccurrences { get; init; }

    public bool Covers(MetadataDifference d)
        => string.Equals(Member, d.Signature, StringComparison.Ordinal)
           && (ObjectKey is null || string.Equals(ObjectKey, d.ObjectKey, StringComparison.Ordinal))
           && (PathPrefix is null || d.Path.StartsWith(PathPrefix, StringComparison.Ordinal));

    public string Describe()
        => Member + (ObjectKey is null ? "" : $" @{ObjectKey}") + (PathPrefix is null ? "" : $" ~{PathPrefix}");
}

/// <summary>What the allowlist made of one comparison.</summary>
public sealed record MetadataAllowlistVerdict(
    IReadOnlyList<MetadataDifference> Undeclared,
    IReadOnlyList<string> UnusedEntries,
    IReadOnlyList<string> ExceededEntries,
    IReadOnlyDictionary<string, int> OccurrencesByEntry)
{
    public bool Ok => Undeclared.Count == 0 && UnusedEntries.Count == 0 && ExceededEntries.Count == 0;
}

public sealed class MetadataDifferenceAllowlist
{
    private static readonly string[] Placeholders = { "", "-", "n/a", "na", "none", "tbd", "todo", "?" };

    public IReadOnlyList<MetadataAllowlistEntry> Entries { get; }

    public MetadataDifferenceAllowlist(IEnumerable<MetadataAllowlistEntry> entries)
    {
        Entries = entries.ToArray();
        foreach (var e in Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Member))
                throw new InvalidDataException("metadata allowlist: an entry has no 'member'.");
            if (Placeholders.Contains(e.Reason.Trim().ToLowerInvariant()))
                throw new InvalidDataException(
                    $"metadata allowlist: entry '{e.Member}' has a placeholder reason " +
                    $"('{e.Reason}'). Every declared difference states why it is tolerated.");
            if (e.Issue is null && !e.CannotExpress)
                throw new InvalidDataException(
                    $"metadata allowlist: entry '{e.Member}' names no issue and is not marked " +
                    "'cannotExpress'. A tolerated DEFECT is tracked; only a permanent limit of " +
                    "the symbol file is not.");
        }
    }

    public static MetadataDifferenceAllowlist Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("differences", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{path}: missing 'differences' array.");
        var entries = JsonSerializer.Deserialize<MetadataAllowlistEntry[]>(arr.GetRawText())
                      ?? Array.Empty<MetadataAllowlistEntry>();
        return new MetadataDifferenceAllowlist(entries);
    }

    /// <summary>Empty allowlist — every difference is undeclared. Used to measure the raw diff.</summary>
    public static MetadataDifferenceAllowlist None { get; } =
        new(Array.Empty<MetadataAllowlistEntry>());

    public MetadataAllowlistVerdict Classify(IReadOnlyList<MetadataDifference> differences)
    {
        var counts = Entries.ToDictionary(e => e.Describe(), _ => 0, StringComparer.Ordinal);
        var undeclared = new List<MetadataDifference>();

        foreach (var d in differences)
        {
            // First entry wins, so a narrowed entry must be listed before a broad one that
            // would otherwise swallow it — and the unused-entry check below is what makes a
            // shadowed entry visible rather than silently inert.
            var hit = Entries.FirstOrDefault(e => e.Covers(d));
            if (hit is null) { undeclared.Add(d); continue; }
            counts[hit.Describe()]++;
        }

        var unused = Entries.Where(e => counts[e.Describe()] == 0)
            .Select(e => e.Describe()).ToArray();
        var exceeded = Entries
            .Where(e => e.MaxOccurrences is int cap && counts[e.Describe()] > cap)
            .Select(e => $"{e.Describe()}: {counts[e.Describe()]} occurrences, declared max {e.MaxOccurrences}")
            .ToArray();

        return new MetadataAllowlistVerdict(undeclared, unused, exceeded, counts);
    }
}
