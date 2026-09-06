// LoudDiagnosisReachesTheUserTests — issue #3068.
//
// What went wrong
// ---------------
// Log.Install() drops any line starting with a `[Tag]` unless --verbose. That filter exists
// for a real reason and this file does NOT weaken it: measured on one healthy, fully-passing
// run of tests/runner-extras/precompiled-table-relation, it suppresses 843 lines —
// 379 [RecordPatches], 371 [Cecil], 65 [Subscribers], 15 [BcRuntime], and a tail of others.
// That volume is exactly what the filter is for.
//
// But a handful of call sites are not chatter. They fire ONLY when a foundational row or
// initialisation step did not happen, and each one says, in its own words, that downstream AL
// will now fail because of it. Their absence does not make the run quieter — it turns a
// cascade of "<Setup Table> does not exist" failures into a mystery and points the reader at
// missing test data instead. #3068 measured the cost: on BC 27.3 codeunit 2
// "Company-Initialize" aborted on every run and the runner said nothing at default verbosity;
// an agent spent a long stretch believing the code path never ran.
//
// The tell was already in the source. Three call sites carry the comment "Loud, never silent"
// — CompanyInitializer, RecordPatches.CompanySystemTable, RecordPatches.UserSystemTable — and
// at the time this file was written ALL THREE were silenced by this filter, three lines below
// a Log.cs comment that says a severity tag is never an internal diagnostic. The author's
// stated intent and the observed behaviour were opposites in every instance.
//
// How it is fixed, and why not by editing Log.cs
// ----------------------------------------------
// Not by adding [CompanyInitializer] & co. to Log's exemption list. Two established decisions
// point the other way:
//
//   * #2750 refused to exempt `[deps]` to surface one corrupt-sidecar message, because that
//     would have surfaced all of DependencyLoader's tier-by-tier internals with it. It
//     re-tagged the one important line instead. LogUserFacingTagsTests pins `[deps]` as
//     deliberately suppressed, and this file does not disturb that.
//   * #2210/#2221/#2239 (CleanRunStartupVerbosityTests) established that hiding a line by
//     editing Log's allowlist is the wrong lever in the other direction too, and moved every
//     line behind an explicit `if (Log.Verbose)` at its own call site.
//
// Both say the same thing: the visibility decision belongs at the call site, not in a regex.
// So each of these lines now uses the already-exempt `[warn]` severity tag, in the
// `[warn] <Component>: <message>` shape ProvisioningCheck and BcAppFallback already use.
// Log.cs's own comment is the rule being applied: "A severity tag is never an internal
// diagnostic — if something is worth calling a warning, it is worth the user seeing it."
//
// Why this test reads the production source
// -----------------------------------------
// Asserting a string literal typed into this file would prove nothing: it would stay green
// with the production call site still tagged [CompanyInitializer]. So each case below names
// a source file and an anchor phrase, pulls THE REAL message out of that call site, and runs
// THAT through the real filter. Re-tag the production line back and this test goes red.
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

// Serial: swaps the process-wide Console writers and Log.Verbose. See ConsoleFilterSerialCollection.
[Collection(ConsoleFilterSerialCollection.Name)]
public sealed class LoudDiagnosisReachesTheUserTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>Push one line through the real Log filter and return what got out.</summary>
    private static string FilterOnce(string line, bool verbose)
    {
        var savedOut = Console.Out;
        var savedErr = Console.Error;
        var savedVerbose = Log.Verbose;
        var sink = new StringWriter();
        try
        {
            Console.SetOut(sink);
            Console.SetError(sink);
            Log.Install();
            Log.Verbose = verbose;
            Console.Error.WriteLine(line);
            return sink.ToString();
        }
        finally
        {
            Log.Verbose = savedVerbose;
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
    }

    /// <summary>
    /// Reconstruct the message a `Console.Error.WriteLine(...)` call site actually emits:
    /// find the anchor phrase, walk back to the enclosing Console.Error.WriteLine, then
    /// concatenate every string literal in that statement and substitute a placeholder for
    /// each interpolation hole. The result is a realistic rendering of the real line — good
    /// enough to be exact about the prefix the filter decides on, and about the body.
    /// </summary>
    private static string ExtractEmittedMessage(string relativePath, string anchor)
    {
        var path = Path.Combine(RepoRoot, relativePath);
        Assert.True(File.Exists(path), $"diagnosis call site source not found: {path}");
        var lines = File.ReadAllLines(path);

        // The anchor phrase usually also appears in the comment block above the call site, so
        // take the first occurrence that actually resolves to a Console.Error.WriteLine — not
        // simply the first occurrence in the file.
        var anchorHits = Enumerable.Range(0, lines.Length)
            .Where(i => lines[i].Contains(anchor, StringComparison.Ordinal))
            .ToList();
        Assert.True(anchorHits.Count > 0,
            $"anchor phrase not found in {relativePath}: \"{anchor}\". If the message was " +
            "reworded, update the anchor — do not delete the case.");

        var start = -1;
        foreach (var hit in anchorHits)
        {
            for (var i = hit; i >= 0 && i >= hit - 10; i--)
            {
                var text = lines[i];
                if (text.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (text.Contains("Console.Error.WriteLine(", StringComparison.Ordinal)) { start = i; break; }
            }
            if (start >= 0) break;
        }
        Assert.True(start >= 0,
            $"the anchor \"{anchor}\" in {relativePath} is no longer inside a " +
            "Console.Error.WriteLine(...) call — the diagnosis may have been deleted or " +
            "routed somewhere this test cannot see.");

        // Collect the statement text up to the balanced closing paren.
        var stmt = new StringBuilder();
        var depth = 0;
        var opened = false;
        for (var i = start; i < lines.Length && i < start + 12; i++)
        {
            stmt.Append(lines[i]).Append('\n');
            foreach (var ch in lines[i])
            {
                if (ch == '(') { depth++; opened = true; }
                else if (ch == ')') depth--;
            }
            if (opened && depth <= 0) break;
        }

        // Concatenate the string literals, unescape, and stand a token in for each hole.
        var literals = Regex.Matches(stmt.ToString(), "\"((?:[^\"\\\\]|\\\\.)*)\"");
        Assert.True(literals.Count > 0, $"no string literal in the statement at {relativePath}:{start + 1}");
        var message = string.Concat(literals.Select(m => m.Groups[1].Value))
            .Replace("\\\"", "\"")
            .Replace("\\n", " ");
        message = Regex.Replace(message, @"\{[^{}]*\}", "<value>");
        return message;
    }

    public static TheoryData<string, string> DiagnosisCallSites() => new()
    {
        // #3068 — the reported one. Codeunit 2 "Company-Initialize" aborted part-way, so every
        // setup table it had not reached yet is missing; without this line the reader sees only
        // the downstream "does not exist" failures and a [test-data] hint suggesting the wrong
        // remedy.
        { "AlRunner/CompanyInitializer.cs", "did not complete" },

        // #2329 — all three branches of the Company-row seed. Comment at the site:
        // "Loud, never silent: without this row Company.Get(CompanyName()) fails for a company
        // every other surface reports as existing, and the failure surfaces several layers up
        // inside Base App code where it reads as a corpus bug."
        { "AlRunner/Patches/RecordPatches.CompanySystemTable.cs", "has no DataAccessSource yet, so the" },
        { "AlRunner/Patches/RecordPatches.CompanySystemTable.cs", "exposes no company name" },
        { "AlRunner/Patches/RecordPatches.CompanySystemTable.cs", "could not seed the Company row" },

        // #2296 — all three branches of the User-row seed. Same shape, same comment: the
        // failure surfaces layers up inside Microsoft AL where it reads as an application bug.
        { "AlRunner/Patches/RecordPatches.UserSystemTable.cs", "there is no skeleton session, so the User row" },
        { "AlRunner/Patches/RecordPatches.UserSystemTable.cs", "exposes no user identity" },
        { "AlRunner/Patches/RecordPatches.UserSystemTable.cs", "was REFUSED and is NOT present" },

        // #2963, and named in #3068 as the same class: System Application module-ownership
        // checks silently decline for the whole run when this row set is not seeded.
        { "AlRunner/Patches/RecordPatches.PublishedApplicationSystemTable.cs", "Published Application rows" },
    };

    /// <summary>
    /// Each of these fires only when something foundational did not happen, and says the run's
    /// AL will now fail because of it. It has to survive the DEFAULT filter — reaching the
    /// user only under --verbose is the defect, not the fix.
    /// </summary>
    [Theory]
    [MemberData(nameof(DiagnosisCallSites))]
    public void RunFatalDiagnosis_SurvivesTheDefaultFilter(string relativePath, string anchor)
    {
        var message = ExtractEmittedMessage(relativePath, anchor);
        var got = FilterOnce(message, verbose: false);
        Assert.Contains(message, got);
    }

    /// <summary>
    /// The same messages under --verbose, so the fix cannot be read as "it was visible anyway".
    /// </summary>
    [Theory]
    [MemberData(nameof(DiagnosisCallSites))]
    public void RunFatalDiagnosis_AlsoSurvivesVerbose(string relativePath, string anchor)
    {
        var message = ExtractEmittedMessage(relativePath, anchor);
        Assert.Contains(message, FilterOnce(message, verbose: true));
    }

    /// <summary>
    /// Every case above names the concrete consequence for the run. A diagnosis that only says
    /// something failed, without saying what stops working, is the version that got ignored.
    /// </summary>
    [Theory]
    [MemberData(nameof(DiagnosisCallSites))]
    public void RunFatalDiagnosis_NamesTheConsequence(string relativePath, string anchor)
    {
        var message = ExtractEmittedMessage(relativePath, anchor);
        Assert.True(
            message.Contains("will fail", StringComparison.OrdinalIgnoreCase)
            || message.Contains("will refuse", StringComparison.OrdinalIgnoreCase)
            || message.Contains("will decline", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} (\"{anchor}\") no longer tells the reader what stops working: {message}");
    }

    /// <summary>
    /// NEGATIVE CONTROL — the filter still does its job. These are the tags measured on one
    /// healthy passing run of tests/runner-extras/precompiled-table-relation (843 suppressed
    /// lines in total). If promoting the diagnoses above had been done by widening Log's
    /// exemption list instead, this is what would have come with them.
    /// </summary>
    [Theory]
    [InlineData("[RecordPatches] BuildNCLMetaTable(18) failed: NullReferenceException: x")]       // 379 lines
    [InlineData("[Cecil] rewriting NavDialog")]                                                   // 371 lines
    [InlineData("[Subscribers] inject: injected=12 failed=0 skipped-no-publisher=3 keys=41")]     //  65 lines
    [InlineData("[BcRuntime] applying patch")]                                                    //  15 lines
    [InlineData("[Dispatch] codeunit 130 method Run")]                                            //   3 lines
    // #2750 decided `deps` stays suppressed and re-tagged the one line that mattered. Measured
    // here: `[deps] tier-2 R2R: Base Application loaded 5 DLL chunk(s)` prints on a HEALTHY,
    // fully-passing run — five chunks is Base Application's normal shape, not a fault — so it
    // is information, not a diagnosis, and it stays where #2750 put it.
    [InlineData("[deps] tier-2 R2R: Base Application loaded 5 DLL chunk(s)")]
    [InlineData("[deps] source-cache HIT: Sidecar Dep v1.0.0.0 key=0123456789ab")]
    public void InternalChatter_IsStillSuppressedByDefault(string line)
    {
        Assert.DoesNotContain(line, FilterOnce(line, verbose: false));
        Assert.Contains(line, FilterOnce(line, verbose: true));
    }
}
