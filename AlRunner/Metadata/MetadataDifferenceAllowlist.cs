// MetadataDifferenceAllowlist — the declared differences between BC's own metadata emitter
// and the runner's SymbolReference-derived derivation, and the ONLY thing that keeps a
// difference from failing the run.
//
// The contract (issue #3533):
//   * every declared difference carries a reason AND says which KIND of reason it is —
//     exactly one of: a tracked defect, a permanent limit of the symbol file, a surface the
//     runner deliberately does not implement, or a limitation of the oracle itself;
//   * an entry that stops matching anything is an ERROR, not a tidy-up — that is how a
//     landed fix forces the allowlist to shrink instead of leaving stale cover behind;
//   * an entry may narrow itself to ONE direction, so a reason that justifies "the runner
//     states something BC does not" cannot also license the reverse.
//
// There is deliberately no occurrence CAP. One was implemented and never used, which made the
// PR claim a ratchet the code did not keep. No entry here has a bound that survives a rebuild:
// the counts move with the BC build and with which apps are bundled (System Application's
// DataClassification is 661 / 663 / 617 across 28.1 / 28.4 / 27.5), so any cap would be a
// number nobody measured. `direction` narrows CATEGORICALLY instead, which is build-independent
// and checkable per difference.
//
// The four kinds are separate because they need DIFFERENT evidence, and conflating them is
// how a recoverable difference gets a permanent licence. Seven TranslationKey entries — 36%
// of the whole diff — were first declared a permanent limit of the symbol file on the
// evidence that it stores no translation keys. True, and the wrong conclusion: the key is
// computed from names rather than stored, and the computation reproduces all 2,153 of them.
// "Nothing is stored" is evidence about storage, never about derivability.
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
    /// The symbol file genuinely cannot express this and it cannot be derived either. Needs
    /// evidence about DERIVABILITY, not merely that nothing is stored.
    /// </summary>
    [JsonPropertyName("cannotExpress")] public bool CannotExpress { get; init; }

    /// <summary>
    /// The runner deliberately does not implement the surface this member serves, so no AL
    /// test can observe it. A scope declaration, not a capability limit — it says nothing
    /// about whether the value could be produced. Requires <see cref="Doc"/>.
    /// </summary>
    [JsonPropertyName("outOfScope")] public bool OutOfScope { get; init; }

    /// <summary>
    /// The GROUND TRUTH is the wrong shape for this member, so the difference is expected by
    /// construction and is not evidence about the runner at all. Requires <see cref="Doc"/>.
    /// </summary>
    [JsonPropertyName("oracleLimitation")] public bool OracleLimitation { get; init; }

    /// <summary>
    /// Where the claim is written down, as <c>docs/&lt;file&gt;.md#anchor</c>. Named
    /// <c>Doc</c> so tools/test_doc_pointers.py validates the path AND the anchor — the same
    /// convention the out-of-scope corpus expectations use.
    /// </summary>
    [JsonPropertyName("Doc")] public string? Doc { get; init; }

    /// <summary>
    /// Narrow the entry to ONE direction, so a reason that justifies one does not license the
    /// other. <c>runner-only</c> covers a difference where BC's side is absent or null and the
    /// runner states something; <c>bc-only</c> is the reverse. Null covers both.
    ///
    /// This is why it exists. The three merged-runtime entries are a permanent, uncapped
    /// licence, and every difference they were written for is BC-absent / runner-present. Left
    /// undirected they also covered "BC emitted a field and the runner built none" — a hard
    /// reader defect, on every table, and the single class of defect the #3545 rewrite is most
    /// likely to introduce. A check that cannot fail reads exactly like a check that passed.
    /// </summary>
    [JsonPropertyName("direction")] public string? Direction { get; init; }

    /// <summary>Only cover differences on this object, e.g. <c>Table 2000000120</c>.</summary>
    [JsonPropertyName("objectKey")] public string? ObjectKey { get; init; }

    /// <summary>Only cover differences whose path starts with this.</summary>
    [JsonPropertyName("pathPrefix")] public string? PathPrefix { get; init; }

    public const string RunnerOnly = "runner-only";
    public const string BcOnly = "bc-only";

    public bool Covers(MetadataDifference d)
        => string.Equals(Member, d.Signature, StringComparison.Ordinal)
           && (ObjectKey is null || string.Equals(ObjectKey, d.ObjectKey, StringComparison.Ordinal))
           && (PathPrefix is null || d.Path.StartsWith(PathPrefix, StringComparison.Ordinal))
           && DirectionCovers(d);

    private bool DirectionCovers(MetadataDifference d) => Direction switch
    {
        null => true,
        RunnerOnly => IsAbsentOrNull(d.Expected),
        BcOnly => IsAbsentOrNull(d.Actual),
        _ => false,   // unreachable: the constructor refuses an unknown direction
    };

    private static bool IsAbsentOrNull(string value)
        => value == MetadataObjectDiff.Absent || value == MetadataObjectDiff.Null;

    public string Describe()
        => Member + (Direction is null ? "" : $" [{Direction}]")
           + (ObjectKey is null ? "" : $" @{ObjectKey}") + (PathPrefix is null ? "" : $" ~{PathPrefix}");
}

/// <summary>What the allowlist made of one comparison.</summary>
public sealed record MetadataAllowlistVerdict(
    IReadOnlyList<MetadataDifference> Undeclared,
    IReadOnlyList<string> UnusedEntries,
    IReadOnlyDictionary<string, int> OccurrencesByEntry)
{
    public bool Ok => Undeclared.Count == 0 && UnusedEntries.Count == 0;
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
            var kinds = (e.Issue is not null ? 1 : 0) + (e.CannotExpress ? 1 : 0)
                        + (e.OutOfScope ? 1 : 0) + (e.OracleLimitation ? 1 : 0);
            if (kinds == 0)
                throw new InvalidDataException(
                    $"metadata allowlist: entry '{e.Member}' says nothing about WHY it is " +
                    "tolerated. Set exactly one of: 'issue' (a tracked defect), 'cannotExpress' " +
                    "(the symbol file cannot express it and it cannot be derived), 'outOfScope' " +
                    "(the runner does not implement the surface, so no AL test can see it), or " +
                    "'oracleLimitation' (the ground truth is the wrong shape for it).");
            if (kinds > 1)
                throw new InvalidDataException(
                    $"metadata allowlist: entry '{e.Member}' claims more than one kind of reason. " +
                    "They need different evidence, so exactly one applies.");
            if (e.Direction is not null
                && e.Direction != MetadataAllowlistEntry.RunnerOnly
                && e.Direction != MetadataAllowlistEntry.BcOnly)
                throw new InvalidDataException(
                    $"metadata allowlist: entry '{e.Member}' has direction '{e.Direction}'. " +
                    $"Use '{MetadataAllowlistEntry.RunnerOnly}' or '{MetadataAllowlistEntry.BcOnly}', " +
                    "or omit it to cover both.");
            if ((e.OutOfScope || e.OracleLimitation) && string.IsNullOrWhiteSpace(e.Doc))
                throw new InvalidDataException(
                    $"metadata allowlist: entry '{e.Member}' declares a scope or oracle boundary " +
                    "with no 'Doc' pointer. Those two are permanent licences to differ, so the " +
                    "claim has to be written down somewhere a reader can find and a reviewer can " +
                    "disagree with — tools/test_doc_pointers.py checks the pointer resolves.");
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

        return new MetadataAllowlistVerdict(undeclared, unused, counts);
    }
}
