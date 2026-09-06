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
    /// <summary>
    /// Test must raise an out-of-scope signal whose reason anchor matches
    /// <c>Reason</c> — either a typed <see cref="RunnerOutOfScopeException"/> or
    /// the documented <c>out-of-scope: &lt;api&gt; — &lt;reason&gt;</c> message
    /// convention that Cecil-injected throw sites carry (#1743).
    /// </summary>
    ExpectOos,
    /// <summary>Test must fail (any non-pass outcome); links to a GH issue tracking the work.</summary>
    ExpectFailKnownGap,
    /// <summary>
    /// Test must fail because the runner INTENDS to answer differently from real
    /// BC on this surface (e.g. docs/scope.md §3.6 task scheduler). Permanent and
    /// declared: no issue to link, a <c>Doc</c> pointer instead (#1741).
    /// </summary>
    ExpectDivergence,
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
    string? Reason,             // required when Mode == ExpectOos or ExpectDivergence
    string? Issue,              // required when Mode == ExpectFailKnownGap; forbidden for ExpectDivergence
    string? Doc,                // required when Mode == ExpectDivergence
    string? Note,
    string SourceFile)          // the .json this entry came from, for diagnostics
{
    public bool MatchesAll => Method == "*";
}

/// <summary>
/// A test codeunit a run actually loaded, as the match audit sees it (#3123).
/// <paramref name="ObjectId"/> is the AL object id parsed from the emitted CLR type
/// name ("Codeunit60810"); null when the type name does not carry one.
/// <paramref name="TestMethods"/> is the UNFILTERED set of [Test] methods the type
/// declares, so a <c>--test</c> filter narrowing what RUNS cannot make an entry look
/// orphaned.
/// <paramref name="MethodsAreComplete"/> is false for a codeunit reconstructed from an
/// EARLIER attempt's carried results (#3168): a carry file records the tests that RAN,
/// which is the codeunit's full declared set only when nothing filtered it. The audit
/// still matches on it — a method that ran certainly exists — but must not tell the
/// reader that a method it cannot see is undeclared.
/// </summary>
public sealed record DiscoveredTestCodeunit(
    int? ObjectId,
    string Name,
    string TypeName,
    IReadOnlyCollection<string> TestMethods,
    bool MethodsAreComplete = true);

/// <summary>
/// An entry the run loaded but could not attach to any discovered test, with a
/// diagnostic saying what WAS found instead (#3123).
/// </summary>
public sealed record UnmatchedExpectation(ExpectationEntry Entry, string Diagnostic);

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

    // #3123: what this run actually loaded. The manifest instance is created once per
    // process and shared across every bundle, so accumulating here — rather than per
    // TestExecutor.Run — is what makes the audit whole-run rather than per-bundle.
    private readonly List<DiscoveredTestCodeunit> _discovered = new();
    private readonly object _discoveredLock = new();

    private ExpectationManifest(IReadOnlyList<ExpectationEntry> entries)
    {
        Entries = entries;
        _byName = entries.ToDictionary(e => (e.CodeunitName, e.Method), e => e);
    }

    /// <summary>
    /// Record a test codeunit this run loaded. Called from the executor at discovery
    /// time, BEFORE any filter narrows which methods run.
    /// </summary>
    public void NoteDiscoveredTestCodeunit(DiscoveredTestCodeunit codeunit)
    {
        lock (_discoveredLock) _discovered.Add(codeunit);
    }

    /// <summary>
    /// Fold an EARLIER watchdog-resume attempt's results into the discovery set (#3168).
    ///
    /// <see cref="NoteDiscoveredTestCodeunit"/> is called by the executor IN THIS PROCESS,
    /// so on its own the audit can only speak for this attempt. A resumed run (#2280) is
    /// several processes: the final one re-runs the bundle with every already-attempted
    /// codeunit excluded and carries the earlier attempts' results in (--merge-results,
    /// Infrastructure.ResumeCarry). Without this, an entry naming a test that ran in an
    /// earlier attempt is reported as matching nothing — "check CodeunitName for a typo"
    /// about an entry that is entirely correct, which is the one distinction this audit
    /// exists to draw. Exactly the shape --count-baseline already handles by summing
    /// `allResults` rather than this attempt's slice (#2719).
    ///
    /// Today the final attempt happens to load the excluded codeunits anyway —
    /// TestExclusionFilter is consulted per METHOD, inside the loop, after discovery is
    /// recorded — so the audit's whole-run claim currently holds by accident. This makes
    /// it hold by construction, and covers the case the accident does not: a bucket the
    /// final attempt never reaches (its own watchdog abort returns from the type loop,
    /// so every later codeunit in that bucket goes unloaded).
    ///
    /// A carried codeunit's method list is what RAN, not what it declares, so it is
    /// marked <see cref="DiscoveredTestCodeunit.MethodsAreComplete"/> = false.
    /// </summary>
    public void NoteDiscoveredFromCarriedResults(IEnumerable<TestResult> carried)
    {
        foreach (var group in carried
            .Where(r => !string.IsNullOrEmpty(r.Codeunit))
            .GroupBy(r => (Type: r.Codeunit, Display: r.CodeunitDisplayName ?? r.Codeunit)))
        {
            // "<ctor>" is the executor's placeholder for a codeunit that would not
            // construct (TestExecutor), not an AL method — matching an entry against it
            // would be matching against a name no AL author can have written.
            var methods = group
                .Select(r => r.Method)
                .Where(m => !string.IsNullOrEmpty(m) && !m.StartsWith("<", StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            NoteDiscoveredTestCodeunit(new DiscoveredTestCodeunit(
                TestExecutor.ParseAlObjectId(group.Key.Type),
                group.Key.Display,
                group.Key.Type,
                methods,
                MethodsAreComplete: false));
        }
    }

    /// <summary>
    /// Entries that no discovered test could ever have consulted (#3123).
    ///
    /// The manifest is loud in both directions for a test it MATCHED — a pass against a
    /// known-gap entry says "remove the entry", an undeclared OOS throw says "add an
    /// entry". An entry that matches NOTHING was the one hole: <see cref="Lookup"/>
    /// returns null for a name it does not hold, the classifier takes its no-entry
    /// branch, and the result is a plain pass or a plain fail. One wrong letter in
    /// <c>CodeunitName</c> or <c>Method</c> therefore converted a declared, tracked gap
    /// into an undeclared one with nothing said anywhere.
    ///
    /// This is deliberately NOT a verdict on its own: an entry naming a corpus codeunit
    /// is legitimately unmatched in a run over a different bundle, which is most runs.
    /// The caller decides what an unmatched entry means — see
    /// <c>--expectations-require-match</c>, which is opted into only by an invocation
    /// that really is expected to cover every entry.
    /// </summary>
    public IReadOnlyList<UnmatchedExpectation> FindUnmatchedEntries()
    {
        DiscoveredTestCodeunit[] discovered;
        lock (_discoveredLock) discovered = _discovered.ToArray();

        var unmatched = new List<UnmatchedExpectation>();
        foreach (var entry in Entries)
        {
            // Mirrors LookupExpectation: entries may be written against the AL object
            // name OR the CLR type name, and "*" matches every test method.
            var named = discovered
                .Where(d => d.Name == entry.CodeunitName || d.TypeName == entry.CodeunitName)
                .ToList();
            if (named.Any(d => entry.MatchesAll || d.TestMethods.Contains(entry.Method)))
                continue;

            unmatched.Add(new UnmatchedExpectation(entry, Explain(entry, named, discovered)));
        }
        return unmatched;
    }

    private static string Explain(
        ExpectationEntry entry,
        IReadOnlyList<DiscoveredTestCodeunit> named,
        IReadOnlyList<DiscoveredTestCodeunit> discovered)
    {
        // Most specific first: the codeunit IS here, the method name is the problem.
        if (named.Count > 0)
        {
            var methods = named.SelectMany(d => d.TestMethods).Distinct().OrderBy(m => m, StringComparer.Ordinal).ToList();
            var shown = methods.Count <= 12
                ? string.Join(", ", methods)
                : string.Join(", ", methods.Take(12)) + $", … ({methods.Count} total)";
            // #3168: every sighting of this codeunit came from a carried attempt, whose
            // method list is what RAN. Saying "declares no test method" off that would be
            // a claim the run cannot support — a filtered-out method is missing from the
            // list for a reason that has nothing to do with the entry.
            if (named.All(d => !d.MethodsAreComplete))
                return $"codeunit \"{entry.CodeunitName}\" was reached only by an earlier resume "
                     + $"attempt, which ran no test method '{entry.Method}'. The methods it ran: "
                     + $"{shown}. A method filtered out of that attempt would look the same, so "
                     + "check the filter before the entry";
            return $"codeunit \"{entry.CodeunitName}\" was loaded but declares no test method "
                 + $"'{entry.Method}'. Its test methods: {shown}";
        }

        // Next: the object id IS here under a different name — the shape a one-letter
        // CodeunitName typo produces.
        var byId = discovered.Where(d => d.ObjectId == entry.CodeunitId).ToList();
        if (byId.Count > 0)
        {
            var names = string.Join(", ", byId.Select(d => $"\"{d.Name}\"").Distinct());
            return $"no codeunit named \"{entry.CodeunitName}\" was loaded; object id "
                 + $"{entry.CodeunitId} was loaded as {names}. Check CodeunitName for a typo";
        }

        return $"no codeunit named \"{entry.CodeunitName}\" (id {entry.CodeunitId}) was loaded in this run";
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
            "expect-divergence" => ExpectationMode.ExpectDivergence,
            "skip" => ExpectationMode.Skip,
            _ => throw new InvalidOperationException(
                $"{relName}[{idx}] ({codeunitName}.{method}): unknown Mode '{modeRaw}' — must be expect-oos, "
                + "expect-fail-known-gap, expect-divergence, or skip"),
        };

        if (mode == ExpectationMode.ExpectOos && string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException(
                $"{relName}[{idx}] ({codeunitName}.{method}): Mode=expect-oos requires non-empty 'Reason'");
        if (mode == ExpectationMode.ExpectFailKnownGap && string.IsNullOrWhiteSpace(issue))
            throw new InvalidOperationException(
                $"{relName}[{idx}] ({codeunitName}.{method}): Mode=expect-fail-known-gap requires non-empty 'Issue'");
        if (mode == ExpectationMode.ExpectDivergence)
        {
            // A divergence is a standing decision, not tracked work, so the entry
            // must carry its own justification: what diverges (Reason) and where
            // that call is written down (Doc). See docs/expectations.md.
            if (string.IsNullOrWhiteSpace(reason))
                throw new InvalidOperationException(
                    $"{relName}[{idx}] ({codeunitName}.{method}): Mode=expect-divergence requires non-empty 'Reason'");
            if (string.IsNullOrWhiteSpace(docAnchor))
                throw new InvalidOperationException(
                    $"{relName}[{idx}] ({codeunitName}.{method}): Mode=expect-divergence requires non-empty 'Doc' "
                    + "pointing at the documented decision (e.g. docs/scope.md#jobs)");
            if (!string.IsNullOrWhiteSpace(issue))
                throw new InvalidOperationException(
                    $"{relName}[{idx}] ({codeunitName}.{method}): Mode=expect-divergence must not carry 'Issue' — "
                    + "an intended divergence has no open work to link. Use expect-fail-known-gap if it is a gap.");
        }

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
    /// <summary>Test failed and a declared-divergence entry applies; counts as pass-divergence.</summary>
    PassDivergence,
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
        // One definition of "this failure is out-of-scope", shared with the
        // reporter: the typed exception OR the documented message convention that
        // Cecil-injected throw sites carry (#1743). Null for a plain failure —
        // an InvalidOperationException without the `out-of-scope: ` prefix is NOT
        // an OOS signal and must never be absorbed as one.
        var signal = outcome.Passed ? null : OutOfScopeMessage.FromException(outcome.Exception);

        // A BC shape gap is NOT an out-of-scope signal and must never be absorbed as one
        // (#2946). It says the runner could not READ BC's internals — a property of which BC
        // build is on disk, not of the runner — so it can be true on one BC leg and false on
        // another in the same run, and "expected" is never an honest thing to call it.
        // Structurally it is already unabsorbable: it is not a RunnerOutOfScopeException and
        // its message does not carry the out-of-scope prefix, so `signal` is null above. What
        // is added here is the DIAGNOSTIC — without it, an expect-oos entry lands in the
        // no-signal branch below, whose advice ("make the throw site raise
        // RunnerOutOfScopeException") is exactly wrong for a layout gap.
        var shapeGap = outcome.Passed ? null : BcShapeGapException.Find(outcome.Exception);

        if (entry == null)
        {
            // No manifest entry — normal pass/fail. Unexpected OOS surfaces as a
            // distinct fail with diagnostic so reviewers know to add an entry.
            if (signal is { } undeclared)
            {
                return new Classification(
                    ExpectationResult.FailManifestDrift,
                    $"Unexpected out-of-scope: {undeclared.Api} (reason: {undeclared.Reason}). "
                    + "Add an expect-oos entry under tests/expectations/ or implement the surface.");
            }
            return new Classification(outcome.Passed ? ExpectationResult.Pass : ExpectationResult.Fail, null);
        }

        switch (entry.Mode)
        {
            case ExpectationMode.Skip:
                return new Classification(ExpectationResult.Skipped, null);

            case ExpectationMode.ExpectOos:
                if (shapeGap != null)
                    return new Classification(
                        ExpectationResult.FailManifestDrift,
                        $"Manifest declares expect-oos (reason: {entry.Reason}) but the runner raised a "
                        + $"{nameof(BcShapeGapException)}: {shapeGap.Surface} — {shapeGap.Member}. "
                        + "That is not a scope boundary, it is the runner failing to read BC's internals "
                        + "on this build, so it must not be declared expected. Fix the reflection target, "
                        + $"or declare it expect-fail-known-gap in {entry.SourceFile} with an open issue.");
                if (outcome.Passed)
                    return new Classification(
                        ExpectationResult.FailManifestDrift,
                        $"Test passed cleanly but manifest declares expect-oos (reason: {entry.Reason}). "
                        + $"Remove the entry from {entry.SourceFile} — runner now supports this surface.");

                if (signal is { } oos)
                {
                    if (ReasonAnchor(oos.Reason) == ReasonAnchor(entry.Reason!))
                        return new Classification(ExpectationResult.PassOos, null);
                    return new Classification(
                        ExpectationResult.FailManifestDrift,
                        $"Expected OOS reason '{entry.Reason}' but runner threw reason '{oos.Reason}'. "
                        + $"Update {entry.SourceFile} or fix the throw site.");
                }

                return new Classification(
                    ExpectationResult.FailManifestDrift,
                    $"Expected an out-of-scope failure (reason: {entry.Reason}) but runner threw "
                    + $"{outcome.Exception?.GetType().Name ?? "<no exception>"} with no out-of-scope signal. "
                    + "Either implement the surface, or make the throw site raise RunnerOutOfScopeException "
                    + "(or the documented 'out-of-scope: <api> — <reason>' message).");

            case ExpectationMode.ExpectFailKnownGap:
                if (outcome.Passed)
                    return new Classification(
                        ExpectationResult.FailManifestDrift,
                        $"Test passed cleanly but manifest declares expect-fail-known-gap "
                        + $"(issue: {entry.Issue}). Remove the entry from {entry.SourceFile} "
                        + "and close the linked issue — the gap appears to be fixed.");
                return new Classification(ExpectationResult.PassKnownGap, null);

            case ExpectationMode.ExpectDivergence:
                if (outcome.Passed)
                    return new Classification(
                        ExpectationResult.FailManifestDrift,
                        $"Test passed cleanly but manifest declares expect-divergence "
                        + $"(reason: {entry.Reason}, doc: {entry.Doc}). Remove the entry from "
                        + $"{entry.SourceFile} — the runner no longer diverges from BC here.");
                // A divergence is "the runner deliberately answers differently"; an
                // out-of-scope throw is a different claim with its own mode, and
                // conflating them would let expect-divergence quietly absorb new OOS
                // surfaces that expect-oos is supposed to declare.
                if (shapeGap != null)
                    return new Classification(
                        ExpectationResult.FailManifestDrift,
                        $"Manifest declares expect-divergence (reason: {entry.Reason}) but the runner raised "
                        + $"a {nameof(BcShapeGapException)}: {shapeGap.Surface} — {shapeGap.Member}. A "
                        + "divergence is an answer the runner gives on purpose; a shape gap is no answer at "
                        + $"all. Fix the reflection target, or declare it expect-fail-known-gap in "
                        + $"{entry.SourceFile} with an open issue.");
                if (signal is { } divergedButOos)
                    return new Classification(
                        ExpectationResult.FailManifestDrift,
                        $"Manifest declares expect-divergence (reason: {entry.Reason}) but the runner raised "
                        + $"an out-of-scope signal: {divergedButOos.Api} (reason: {divergedButOos.Reason}). "
                        + $"Declare it expect-oos in {entry.SourceFile} instead.");
                return new Classification(ExpectationResult.PassDivergence, null);

            default:
                throw new InvalidOperationException($"Unhandled ExpectationMode: {entry.Mode}");
        }
    }

    // Manifest entries hold the bare reason anchor — a docs/scope.md section for a permanent
    // refusal, or "not-yet-implemented" for an in-scope surface the runner cannot answer for
    // yet — while throw sites append free-text detail after an em-dash ("<anchor> — only
    // InnerJoin and …"). Reasons match on the anchor.
    private static string ReasonAnchor(string reason)
    {
        int sep = reason.IndexOf(" — ", StringComparison.Ordinal);
        return (sep >= 0 ? reason[..sep] : reason).Trim();
    }
}
