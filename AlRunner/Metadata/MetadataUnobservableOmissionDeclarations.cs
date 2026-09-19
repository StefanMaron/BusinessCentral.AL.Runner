// MetadataUnobservableOmissionDeclarations — what MetadataDocumentPresenceDiff finds, declared.
//
// Separate from MetadataDifferenceAllowlist on purpose, and the separation is the point (#4357).
// An allowlist entry says "these two derivations DISAGREE here, and that is tolerated". An entry
// here says something weaker and quite different: "these two derivations AGREE here, and the
// agreement is not evidence, because one side never stated the attribute and BC's own reader
// produces the same object either way."
//
// Folding the two together would spell a third state as one of the other two
// (.claude/rules/guards-need-a-third-state.md). It would also be read wrongly in the direction
// that costs something: a reviewer scanning the allowlist reads every entry as a known
// disagreement, and would look for a value to fix.
//
// The contract mirrors the allowlist's, because the failure mode is the same: an undeclared
// omission fails the run, and an entry matching nothing is an error unless it declares itself
// version-contingent. So a fix that makes the runner state an attribute BC states must delete
// the entry, rather than leaving stale cover behind.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AlRunner.Metadata;

/// <summary>One declared unobservable omission.</summary>
public sealed class MetadataUnobservableOmissionEntry
{
    /// <summary>
    /// <c>ObjectKind.Element.Attribute</c>, e.g. <c>Page.Properties.AnalysisModeEnabled</c>.
    /// The object kind is part of the key because one attribute name means different things on
    /// different object kinds — <c>Controls/@Editable</c> on a page and on a report's request
    /// page are read by different types.
    /// </summary>
    [JsonPropertyName("member")] public string Member { get; init; } = "";

    /// <summary>Which side states the attribute the other omits.</summary>
    /// <seealso cref="MetadataDocumentPresenceDiff.ExpectedSide"/>
    [JsonPropertyName("statedBy")] public string StatedBy { get; init; } = "";

    /// <summary>Why this is recorded rather than fixed. A placeholder is refused.</summary>
    [JsonPropertyName("reason")] public string Reason { get; init; } = "";

    /// <summary>The issue tracking whether the runner should state it.</summary>
    [JsonPropertyName("issue")] public int? Issue { get; init; }

    /// <summary>Exempt from the unused-entry check, for the reason the allowlist's flag of the
    /// same name documents: the population moves with the BC build.</summary>
    [JsonPropertyName("versionContingent")] public bool VersionContingent { get; init; }

    /// <summary>Where the claim is written down, as <c>docs/&lt;file&gt;.md#anchor</c>.</summary>
    [JsonPropertyName("Doc")] public string? Doc { get; init; }

    public bool Covers(MetadataUnobservableOmission o, string objectKind)
        => string.Equals(Member, objectKind + "." + o.Signature, StringComparison.Ordinal)
           && string.Equals(StatedBy, o.StatedBy, StringComparison.Ordinal);

    public string Describe() => $"{Member} [{StatedBy}]" + (VersionContingent ? " (version-contingent)" : "");
}

/// <summary>What the declarations made of one comparison.</summary>
public sealed record MetadataUnobservableVerdict(
    IReadOnlyList<MetadataUnobservableOmission> Undeclared,
    IReadOnlyList<string> UnusedEntries,
    IReadOnlyDictionary<string, int> OccurrencesByEntry)
{
    public bool Ok => Undeclared.Count == 0 && UnusedEntries.Count == 0;
}

public sealed class MetadataUnobservableOmissionDeclarations
{
    private static readonly string[] Placeholders = { "", "-", "n/a", "na", "none", "tbd", "todo", "?" };

    public IReadOnlyList<MetadataUnobservableOmissionEntry> Entries { get; }

    public MetadataUnobservableOmissionDeclarations(IEnumerable<MetadataUnobservableOmissionEntry> entries)
    {
        Entries = entries.ToArray();
        foreach (var e in Entries)
        {
            if (string.IsNullOrWhiteSpace(e.Member))
                throw new InvalidDataException("unobservable omissions: an entry has no 'member'.");
            if (e.StatedBy != MetadataDocumentPresenceDiff.ExpectedSide
                && e.StatedBy != MetadataDocumentPresenceDiff.ActualSide)
                throw new InvalidDataException(
                    $"unobservable omissions: entry '{e.Member}' has statedBy '{e.StatedBy}'. Use " +
                    $"'{MetadataDocumentPresenceDiff.ExpectedSide}' (BC states it, the runner omits " +
                    $"it) or '{MetadataDocumentPresenceDiff.ActualSide}' (the reverse). The two need " +
                    "different fixes, so one entry must not license both.");
            if (Placeholders.Contains(e.Reason.Trim().ToLowerInvariant()))
                throw new InvalidDataException(
                    $"unobservable omissions: entry '{e.Member}' has a placeholder reason " +
                    $"('{e.Reason}').");
            if (e.VersionContingent && string.IsNullOrWhiteSpace(e.Doc))
                throw new InvalidDataException(
                    $"unobservable omissions: entry '{e.Member}' is versionContingent with no " +
                    "'Doc' pointer, so nothing says why its population moves with the BC build.");
        }
    }

    public static MetadataUnobservableOmissionDeclarations Load(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("omissions", out var arr)
            || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{path}: missing 'omissions' array.");
        var entries = JsonSerializer.Deserialize<MetadataUnobservableOmissionEntry[]>(arr.GetRawText())
                      ?? Array.Empty<MetadataUnobservableOmissionEntry>();
        return new MetadataUnobservableOmissionDeclarations(entries);
    }

    /// <summary>
    /// The object kind an omission is about, taken from the first word of its object key —
    /// <c>Page 8350</c> is <c>Page</c>. Returns the whole key when it has no space, so a key
    /// shape nobody anticipated produces an unmatched entry rather than being silently grouped
    /// under an empty kind.
    /// </summary>
    public static string ObjectKindOf(MetadataUnobservableOmission omission)
    {
        var space = omission.ObjectKey.IndexOf(' ');
        return space < 0 ? omission.ObjectKey : omission.ObjectKey[..space];
    }

    public MetadataUnobservableVerdict Classify(IReadOnlyList<MetadataUnobservableOmission> omissions)
    {
        var counts = Entries.ToDictionary(e => e.Describe(), _ => 0, StringComparer.Ordinal);
        var undeclared = new List<MetadataUnobservableOmission>();

        foreach (var o in omissions)
        {
            var kind = ObjectKindOf(o);
            var hit = Entries.FirstOrDefault(e => e.Covers(o, kind));
            if (hit is null) { undeclared.Add(o); continue; }
            counts[hit.Describe()]++;
        }

        var unused = Entries.Where(e => !e.VersionContingent && counts[e.Describe()] == 0)
            .Select(e => e.Describe()).ToArray();

        return new MetadataUnobservableVerdict(undeclared, unused, counts);
    }
}
