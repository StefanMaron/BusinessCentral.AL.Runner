// ExpectationManifest — declares expected outcomes for tests in the
// `tests/al-language` submodule. See docs/expectations.md for the schema and
// result-classification table.
//
// The corpus is the canonical spec of AL behaviour against real BC; it does
// not know about the runner. The runner declares — out-of-band — which tests
// are out-of-scope-by-design, which are known gaps tracked by a GH issue, and
// which must be skipped. The reporter consults this manifest at result time.
//
// Format mirrors Microsoft's ALAppExtensions/Build/DisabledTests/ shape
// (codeunitId / CodeunitName / Method) plus runner-extension fields
// (Mode / Reason / Issue / Doc / Note).
//
// Loader fails loudly on schema violations — startup aborts rather than
// silently ignoring a malformed entry.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace AlRunner.Infrastructure;

public enum ExpectationMode
{
    /// <summary>Test must throw <see cref="RunnerOutOfScopeException"/> with the matching <c>Reason</c>.</summary>
    ExpectOos,
    /// <summary>Test must fail (any non-pass outcome); links to a GH issue tracking the work.</summary>
    ExpectFailKnownGap,
    /// <summary>Test must not be invoked.</summary>
    Skip,
}

/// <summary>
/// One row in an expectations JSON file. See docs/expectations.md.
/// </summary>
public sealed record ExpectationEntry(
    int CodeunitId,
    string CodeunitName,
    string Method,              // "*" matches every test method in the codeunit
    ExpectationMode Mode,
    string? Reason,             // required when Mode == ExpectOos
    string? Issue,              // required when Mode == ExpectFailKnownGap
    string? Doc,
    string? Note,
    string SourceFile)          // the .json this entry came from, for diagnostics
{
    public bool MatchesAll => Method == "*";
}

/// <summary>
/// Loaded view of all expectation files under a directory (default
/// <c>tests/expectations/</c>). Use <see cref="Lookup"/> to query expectations
/// at result-classification time.
/// </summary>
public sealed class ExpectationManifest
{
    // (codeunitName, method) → entry. Method "*" entries stored under method = "*".
    private readonly Dictionary<(string Codeunit, string Method), ExpectationEntry> _byName;

    public IReadOnlyList<ExpectationEntry> Entries { get; }

    private ExpectationManifest(IReadOnlyList<ExpectationEntry> entries)
    {
        Entries = entries;
        _byName = entries.ToDictionary(e => (e.CodeunitName, e.Method), e => e);
    }

    /// <summary>
    /// Look up the expectation for a (codeunit, method) pair. Returns the
    /// method-specific entry if present; otherwise the wildcard ("*") entry
    /// for that codeunit; otherwise null (no expectation declared).
    /// </summary>
    public ExpectationEntry? Lookup(string codeunitName, string method)
    {
        if (_byName.TryGetValue((codeunitName, method), out var exact)) return exact;
        if (_byName.TryGetValue((codeunitName, "*"), out var wildcard)) return wildcard;
        return null;
    }

    /// <summary>
    /// Load every <c>*.json</c> file under <paramref name="manifestDir"/>.
    /// Returns an empty manifest if the directory does not exist. Throws on
    /// schema violations — runner startup must not silently ignore a
    /// malformed file.
    /// </summary>
    public static ExpectationManifest LoadFromDirectory(string manifestDir)
    {
        if (!Directory.Exists(manifestDir))
            return new ExpectationManifest(Array.Empty<ExpectationEntry>());

        var entries = new List<ExpectationEntry>();
        var dupeCheck = new HashSet<(string, string, string)>();   // (cu, method, file)

        foreach (var path in Directory.EnumerateFiles(manifestDir, "*.json").OrderBy(p => p))
        {
            var rel = Path.GetFileName(path);
            var fileEntries = LoadFile(path, rel);
            foreach (var entry in fileEntries)
            {
                var key = (entry.CodeunitName, entry.Method, entry.SourceFile);
                if (!dupeCheck.Add(key))
                    throw new InvalidOperationException(
                        $"Duplicate expectation in {rel}: {entry.CodeunitName}.{entry.Method}");
                entries.Add(entry);
            }
        }

        // Cross-file duplicate check: same (codeunit, method) declared in two files.
        var crossDupes = entries
            .GroupBy(e => (e.CodeunitName, e.Method))
            .Where(g => g.Count() > 1)
            .ToList();
        if (crossDupes.Count > 0)
        {
            var first = crossDupes[0];
            var files = string.Join(", ", first.Select(e => e.SourceFile));
            throw new InvalidOperationException(
                $"Expectation for {first.Key.CodeunitName}.{first.Key.Method} declared in multiple files: {files}");
        }

        return new ExpectationManifest(entries);
    }

    private static List<ExpectationEntry> LoadFile(string path, string relName)
    {
        var raw = File.ReadAllText(path);
        JsonDocument doc;
        try { doc = JsonDocument.Parse(raw); }
        catch (JsonException jx)
        {
            throw new InvalidOperationException($"Invalid JSON in {relName}: {jx.Message}", jx);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException($"{relName}: root must be a JSON array of entries");

            var result = new List<ExpectationEntry>(doc.RootElement.GetArrayLength());
            int idx = -1;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                idx++;
                if (el.ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException($"{relName}[{idx}]: must be a JSON object");
                result.Add(ParseEntry(el, relName, idx));
            }
            return result;
        }
    }

    private static ExpectationEntry ParseEntry(JsonElement el, string relName, int idx)
    {
        string Req(string name)
        {
            if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String)
                throw new InvalidOperationException($"{relName}[{idx}]: missing required string field '{name}'");
            return v.GetString()!;
        }
        int ReqInt(string name)
        {
            if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number)
                throw new InvalidOperationException($"{relName}[{idx}]: missing required integer field '{name}'");
            return v.GetInt32();
        }
        string? Opt(string name)
        {
            if (!el.TryGetProperty(name, out var v)) return null;
            return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }

        var codeunitId = ReqInt("codeunitId");
        var codeunitName = Req("CodeunitName");
        var method = Req("Method");
        var modeRaw = Req("Mode");
        var reason = Opt("Reason");
        var issue = Opt("Issue");
        var docAnchor = Opt("Doc");
        var note = Opt("Note");

        var mode = modeRaw switch
        {
            "expect-oos" => ExpectationMode.ExpectOos,
            "expect-fail-known-gap" => ExpectationMode.ExpectFailKnownGap,
            "skip" => ExpectationMode.Skip,
            _ => throw new InvalidOperationException(
                $"{relName}[{idx}] ({codeunitName}.{method}): unknown Mode '{modeRaw}' — must be expect-oos, expect-fail-known-gap, or skip"),
        };

        if (mode == ExpectationMode.ExpectOos && string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException(
                $"{relName}[{idx}] ({codeunitName}.{method}): Mode=expect-oos requires non-empty 'Reason'");
        if (mode == ExpectationMode.ExpectFailKnownGap && string.IsNullOrWhiteSpace(issue))
            throw new InvalidOperationException(
                $"{relName}[{idx}] ({codeunitName}.{method}): Mode=expect-fail-known-gap requires non-empty 'Issue'");

        return new ExpectationEntry(
            codeunitId, codeunitName, method, mode, reason, issue, docAnchor, note, relName);
    }
}

/// <summary>
/// Classification of a single test outcome against the manifest. Returned by
/// <see cref="ExpectationClassifier.Classify"/>; consumed by the reporter to
/// decide which bucket the result lands in.
/// </summary>
public enum ExpectationResult
{
    /// <summary>Test passed and no manifest entry applies. Normal pass.</summary>
    Pass,
    /// <summary>Test failed and no manifest entry applies. Normal fail.</summary>
    Fail,
    /// <summary>Test threw <see cref="RunnerOutOfScopeException"/> with matching reason; counts as pass-oos.</summary>
    PassOos,
    /// <summary>Test failed and a known-gap entry applies; counts as pass-known-gap.</summary>
    PassKnownGap,
    /// <summary>Test was not invoked because a skip entry applies.</summary>
    Skipped,
    /// <summary>Manifest-vs-reality drift; the reporter prints <see cref="ClassificationDiagnostic"/>.</summary>
    FailManifestDrift,
}

public sealed record TestOutcome(
    string CodeunitName,
    string Method,
    bool Passed,
    Exception? Exception);

public sealed record Classification(
    ExpectationResult Result,
    string? Diagnostic);

public static class ExpectationClassifier
{
    /// <summary>
    /// Decide how a test outcome should be classified given the manifest entry
    /// (if any). Implements the table in docs/expectations.md.
    /// </summary>
    public static Classification Classify(TestOutcome outcome, ExpectationEntry? entry)
    {
        if (entry == null)
        {
            // No manifest entry — normal pass/fail. Unexpected OOS surfaces as a
            // distinct fail with diagnostic so reviewers know to add an entry.
            if (!outcome.Passed && outcome.Exception is RunnerOutOfScopeException oos)
            {
                return new Classification(
                    ExpectationResult.FailManifestDrift,
                    $"Unexpected out-of-scope: {oos.Api} (reason: {oos.Reason}). "
                    + "Add an expect-oos entry under tests/expectations/ or implement the surface.");
            }
            return new Classification(outcome.Passed ? ExpectationResult.Pass : ExpectationResult.Fail, null);
        }

        switch (entry.Mode)
        {
            case ExpectationMode.Skip:
                return new Classification(ExpectationResult.Skipped, null);

            case ExpectationMode.ExpectOos:
                if (outcome.Passed)
                    return new Classification(
                        ExpectationResult.FailManifestDrift,
                        $"Test passed cleanly but manifest declares expect-oos (reason: {entry.Reason}). "
                        + $"Remove the entry from {entry.SourceFile} — runner now supports this surface.");

                if (outcome.Exception is RunnerOutOfScopeException oos)
                {
                    if (oos.Reason == entry.Reason)
                        return new Classification(ExpectationResult.PassOos, null);
                    return new Classification(
                        ExpectationResult.FailManifestDrift,
                        $"Expected OOS reason '{entry.Reason}' but runner threw reason '{oos.Reason}'. "
                        + $"Update {entry.SourceFile} or fix the throw site.");
                }

                return new Classification(
                    ExpectationResult.FailManifestDrift,
                    $"Expected RunnerOutOfScopeException (reason: {entry.Reason}) but runner threw "
                    + $"{outcome.Exception?.GetType().Name ?? "<no exception>"}. "
                    + "Either implement the surface, or replace the failure with a typed RunnerOutOfScopeException.");

            case ExpectationMode.ExpectFailKnownGap:
                if (outcome.Passed)
                    return new Classification(
                        ExpectationResult.FailManifestDrift,
                        $"Test passed cleanly but manifest declares expect-fail-known-gap "
                        + $"(issue: {entry.Issue}). Remove the entry from {entry.SourceFile} "
                        + "and close the linked issue — the gap appears to be fixed.");
                return new Classification(ExpectationResult.PassKnownGap, null);

            default:
                throw new InvalidOperationException($"Unhandled ExpectationMode: {entry.Mode}");
        }
    }
}
