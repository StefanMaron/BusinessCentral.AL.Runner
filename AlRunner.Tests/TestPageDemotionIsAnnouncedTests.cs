// TestPageDemotionIsAnnouncedTests — issue #2461.
//
// What went wrong
// ---------------
// A TestPage that cannot be driven live falls back to MockITestPage, whose GetAction returns a
// MockITestAction with `Enabled => true` and an empty `Invoke()`. That degradation is allowed by
// loud-failures.md ONLY because it announces itself. It did not: measured on BC 28.1 running one
// test that opens Base App page 977, neither `[RunnerPageInstance] page 977: could not build the
// record-less AL page object ...` nor `[TestPage] ...; using navigation mock.` appeared in the run
// log, while both call sites demonstrably executed (a File.AppendAllText probe in the same catch
// block in the same build printed both). #2451 recorded 33 failures across seven symptoms; ten had
// this one cause and nothing in the output pointed at it.
//
// Where the lines were actually going — measured, not assumed
// ----------------------------------------------------------
// Not a child process, and not the choice of stream. The call sites carried a comment, repeated
// four times across three files, asserting the opposite:
//
//     // stdout on purpose throughout this class: the test-execution child's stderr is
//     // not captured, so a Console.Error line would be invisible exactly when needed.
//
// There is no test-execution child: `Console.SetOut`/`SetError` appears nowhere in AlRunner except
// Log.cs, Program.cs's `--output-json` redirect, and the watch-mode dashboard silencer. What eats
// these lines is Log.Install()'s FilteredWriter, which wraps BOTH streams and drops any line
// matching `^\[Tag]` unless --verbose. `[RunnerPageInstance]` and `[TestPage]` both match and are
// on neither exemption. So stdout and stderr were always equally suppressed, and switching between
// them — which is what the comment spends its words on — could never have made a difference.
//
// The issue's own control observation fits exactly: `[page-metadata]` DID survive, because the tag
// character class `[A-Za-z0-9._+]` has no hyphen, so a hyphenated tag fails the pattern and passes
// through. Punctuation, not intent, was deciding which half of this degradation was visible.
// Log.cs's comment documents that accident and #2257 owns the general case.
//
// How it is fixed, and why not by editing Log.cs
// ---------------------------------------------
// Same answer #3068 reached for the identical class of bug, and it is binding precedent here:
// re-tag the call site, never widen the regex. #2750 refused to exempt `[deps]` to surface one
// message because that would surface all of DependencyLoader's internals with it; #2210/#2221/#2239
// established the mirror image for hiding a line. Both say the visibility decision belongs at the
// call site. Exempting `[RunnerPageInstance]` wholesale would be especially wrong here: that tag
// also carries per-control AdoptFromHost tracing, which is exactly the chatter the filter is for.
//
// So the lines that announce a DEMOTION — and only those — use the already-exempt `[warn]` severity
// tag in the `[warn] <Component>: <message>` shape ProvisioningCheck, BcAppFallback and the #3068
// call sites already use. Log.cs's own comment is the rule being applied: "A severity tag is never
// an internal diagnostic — if something is worth calling a warning, it is worth the user seeing it."
//
// Why this test reads the production source
// -----------------------------------------
// Asserting a literal typed into this file would prove nothing — it would stay green with the
// production call site still tagged `[RunnerPageInstance]`, which is precisely the bug. Each case
// below names a source file and an anchor phrase, pulls THE REAL message out of that call site, and
// pushes THAT through the REAL filter. Re-tag any production line back and this test goes red.
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

// Serial: swaps the process-wide Console writers and Log.Verbose. See ConsoleFilterSerialCollection.
[Collection(ConsoleFilterSerialCollection.Name)]
public sealed class TestPageDemotionIsAnnouncedTests
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
            // Written to stdout deliberately: these call sites use Console.Out, and the point of
            // this file is that the filter wraps BOTH streams, so the stream is not the variable.
            Console.Out.WriteLine(line);
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
    /// Reconstruct the message a <c>Console.Out.WriteLine(...)</c> / <c>Console.Error.WriteLine(...)</c>
    /// call site actually emits: find the anchor phrase, walk back to the enclosing write, then
    /// concatenate every string literal in that statement, substituting a placeholder for each
    /// interpolation hole. Exact about the prefix the filter decides on, which is the whole point.
    /// </summary>
    private static string ExtractEmittedMessage(string relativePath, string anchor)
    {
        var path = Path.Combine(RepoRoot, relativePath);
        Assert.True(File.Exists(path), $"demotion call site source not found: {path}");
        var lines = File.ReadAllLines(path);

        // The anchor usually also appears in the comment block above the call site, so take the
        // first occurrence that actually resolves to a console write — not the first in the file.
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
                if (text.Contains("Console.Out.WriteLine(", StringComparison.Ordinal)
                    || text.Contains("Console.Error.WriteLine(", StringComparison.Ordinal)) { start = i; break; }
            }
            if (start >= 0) break;
        }
        Assert.True(start >= 0,
            $"the anchor \"{anchor}\" in {relativePath} is no longer inside a console write — the " +
            "demotion announcement may have been deleted or routed somewhere this test cannot see.");

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

        var literals = Regex.Matches(stmt.ToString(), "\"((?:[^\"\\\\]|\\\\.)*)\"");
        Assert.True(literals.Count > 0, $"no string literal in the statement at {relativePath}:{start + 1}");
        var message = string.Concat(literals.Select(m => m.Groups[1].Value))
            .Replace("\\\"", "\"")
            .Replace("\\n", " ");
        message = Regex.Replace(message, @"\{[^{}]*\}", "<value>");
        return message;
    }

    /// <summary>
    /// Every site where a TestPage silently stops being the real page. Each one hands the AL a
    /// substitute that answers questions it cannot answer — the navigation mock outright, or a
    /// record-only / trigger-less page — so each has to reach the user at DEFAULT verbosity.
    /// </summary>
    public static TheoryData<string, string> DemotionCallSites() => new()
    {
        // The navigation mock itself: MockITestPage.GetAction hands back a MockITestAction whose
        // Enabled is a constant true and whose Invoke() is a literal no-op.
        { "AlRunner/Patches/CodeunitPatches.cs", "using the navigation mock" },

        // TryCreate — the record-bearing path. Falls back to record-only access, so every control
        // bound to a page variable rather than a Rec field reads as absent.
        { "AlRunner/Patches/RunnerPageInstance.cs", "no compiled Page" },
        { "AlRunner/Patches/RunnerPageInstance.cs", "has no (ITreeObject, NavRecord) ctor" },
        { "AlRunner/Patches/RunnerPageInstance.cs", "initialised but published no " },
        { "AlRunner/Patches/RunnerPageInstance.cs", "could not build the AL page object" },

        // TryCreateRecordless — the path page 977 took in the report on #2461.
        { "AlRunner/Patches/RunnerPageInstance.cs", "has no (ITreeObject) ctor" },
        { "AlRunner/Patches/RunnerPageInstance.cs", "published no source-expression table for a record-less" },
        { "AlRunner/Patches/RunnerPageInstance.cs", "could not build the record-less AL page object" },

        // Page extensions whose object could not be constructed: their triggers stay unreachable,
        // so an OnAction the AL is relying on never runs.
        { "AlRunner/Patches/RunnerPageInstance.cs", "its triggers stay unreachable" },
        { "AlRunner/Patches/RunnerPageInstance.cs", "no live base page " },

        // The request-page equivalent: a report's request page whose form could not be adopted
        // resolves no control at all. Same shape, same filter, found by the same sweep.
        { "AlRunner/Patches/RequestPageTestPage.cs", "could not adopt the request-page form" },
    };

    /// <summary>
    /// The bug, stated as a test. Each of these fires exactly when a TestPage stops being the page
    /// the test asked for; reaching the user only under --verbose is the defect, not the fix.
    /// </summary>
    [Theory]
    [MemberData(nameof(DemotionCallSites))]
    public void Demotion_IsAnnouncedAtDefaultVerbosity(string relativePath, string anchor)
    {
        var message = ExtractEmittedMessage(relativePath, anchor);
        var got = FilterOnce(message, verbose: false);
        Assert.Contains(message, got);
    }

    /// <summary>
    /// The same messages under --verbose, so the fix cannot be read as "it was visible anyway".
    /// </summary>
    [Theory]
    [MemberData(nameof(DemotionCallSites))]
    public void Demotion_IsAlsoAnnouncedUnderVerbose(string relativePath, string anchor)
    {
        var message = ExtractEmittedMessage(relativePath, anchor);
        Assert.Contains(message, FilterOnce(message, verbose: true));
    }

    /// <summary>
    /// A demotion notice has to identify WHICH object was demoted, or the reader cannot tell which
    /// of the run's TestPages answered from a substitute — the specific thing missing in #2451,
    /// where ten failures shared this cause and nothing in the output distinguished them. Every
    /// message above interpolates the page id, or the report id for the request-page case.
    /// </summary>
    [Theory]
    [MemberData(nameof(DemotionCallSites))]
    public void Demotion_NamesTheObjectItDemoted(string relativePath, string anchor)
    {
        var message = ExtractEmittedMessage(relativePath, anchor);
        Assert.True(
            message.Contains("page <value>", StringComparison.Ordinal)
            || message.Contains("Page<value>", StringComparison.Ordinal)
            || message.Contains("report <value>", StringComparison.Ordinal)
            || message.Contains("pageextension <value>", StringComparison.Ordinal),
            $"{relativePath} (\"{anchor}\") no longer names the object it demoted: {message}");
    }

    /// <summary>
    /// A demotion notice also has to say WHAT stops working, or the reader learns that something
    /// failed without learning that the answers they are about to read came from a substitute.
    /// This is the property that separates these lines from ordinary internal chatter, and it is
    /// why they are exempt from the filter at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(DemotionCallSites))]
    public void Demotion_NamesTheConsequence(string relativePath, string anchor)
    {
        var message = ExtractEmittedMessage(relativePath, anchor);
        Assert.True(
            message.Contains("falls back", StringComparison.OrdinalIgnoreCase)
            || message.Contains("stay unreachable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("stay unresolvable", StringComparison.OrdinalIgnoreCase)
            || message.Contains("nothing left to answer from", StringComparison.OrdinalIgnoreCase)
            || message.Contains("navigation mock", StringComparison.OrdinalIgnoreCase),
            $"{relativePath} (\"{anchor}\") no longer tells the reader what stops working: {message}");
    }

    /// <summary>
    /// NEGATIVE CONTROL, and the reason this fix re-tags call sites instead of exempting
    /// `[RunnerPageInstance]` in Log.cs. That tag also carries per-control AdoptFromHost and
    /// option-caption tracing — real chatter, already behind AL_RUNNER_TRACE_PAGE_METADATA at its
    /// own call sites — which a blanket exemption would have promoted along with the demotions.
    /// These lines are copied from the real tracing call sites in RunnerPageInstance.cs and MUST
    /// stay suppressed by default.
    /// </summary>
    [Theory]
    [InlineData("[RunnerPageInstance] AdoptFromHost control 42: reused cached reified subpage")]
    [InlineData("[RunnerPageInstance] page 977: built, 12 source expression(s): a, b")]
    // NOT [option-captions]: that tag is hyphenated, and the filter's tag character class has no
    // hyphen, so it passes today by punctuation rather than by intent. #2257 owns that set; using
    // it as a control here would assert the accident instead of the filter.
    [InlineData("[RunnerPageInstance] AdoptFromHost control 42: adopted subpage Page977, reifying")]
    public void PageTracingChatter_IsStillSuppressedByDefault(string line)
    {
        Assert.DoesNotContain(line, FilterOnce(line, verbose: false));
        Assert.Contains(line, FilterOnce(line, verbose: true));
    }

    /// <summary>
    /// The false premise that kept this bug alive, pinned so it cannot come back. The call sites
    /// said stderr "is not captured" and chose stdout for that reason; the filter wraps BOTH, so
    /// the two streams are equally suppressed and the stream was never the variable. If this ever
    /// stops holding, the comments explaining the `[warn]` tag need revisiting — not the tag.
    /// </summary>
    [Fact]
    public void TheFilterSuppressesTaggedLines_OnBothStreams()
    {
        const string tagged = "[RunnerPageInstance] page 977: could not build the AL page object";

        var savedOut = Console.Out;
        var savedErr = Console.Error;
        var savedVerbose = Log.Verbose;
        var sink = new StringWriter();
        try
        {
            Console.SetOut(sink);
            Console.SetError(sink);
            Log.Install();
            Log.Verbose = false;
            Console.Out.WriteLine(tagged);
            Console.Error.WriteLine(tagged);
            Assert.DoesNotContain(tagged, sink.ToString());

            // ...and the `[warn]` shape the fix uses survives on both, so the choice of stream
            // stays a non-decision rather than becoming a new hidden dependency.
            const string warned = "[warn] RunnerPageInstance: page 977 demoted";
            Console.Out.WriteLine(warned);
            Assert.Contains(warned, sink.ToString());
            var afterOut = sink.ToString();
            Console.Error.WriteLine(warned);
            Assert.True(sink.ToString().Length > afterOut.Length,
                "the [warn] line written to stderr did not reach the sink");
        }
        finally
        {
            Log.Verbose = savedVerbose;
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
    }
}
