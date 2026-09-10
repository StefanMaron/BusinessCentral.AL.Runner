// ProcedureBoundPropertyWarningTests — issue #3731.
//
// `Enabled = SomeProcedure()` on an action compiles with warning AL0573 ("Procedure calls is not
// valid for client expressions"), and real BC never evaluates the expression: the action reads
// Enabled = false and its OnAction is skipped, though the procedure returns true unconditionally
// (measured on BC 28.4.53241.0 — issue #3731). The runner now answers false too, which is silent,
// exactly as BC is silent. The warning is the only thing that tells the AL author their procedure
// is not being called, so two properties of it are pinned here:
//
//   * it survives Log's DEFAULT filter — a line only --verbose shows is not a warning;
//   * it fires ONCE per (page, element, property), so a bundle opening the same page in ten tests
//     prints one copy, not ten. The AL side of this (tests/runner-extras/testpage-procedure-bound-property)
//     cannot assert either: AL cannot read the runner's own stderr.
using System.Text.RegularExpressions;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Serial: swaps the process-wide Console writers and Log.Verbose. See ConsoleFilterSerialCollection.
[Collection(ConsoleFilterSerialCollection.Name)]
public sealed class ProcedureBoundPropertyWarningTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    /// <summary>Run one line through the real Log filter and return what got out.</summary>
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

    /// <summary>Capture what the real call site writes, by calling it.</summary>
    private static string EmitOnce(string pageId, int elementId, string propertyName, out bool printed)
    {
        var savedErr = Console.Error;
        var sink = new StringWriter();
        try
        {
            Console.SetError(sink);
            printed = RunnerPageInstance.WarnOnceAboutDroppedClientExpression(pageId, elementId, propertyName);
            return sink.ToString();
        }
        finally
        {
            Console.SetError(savedErr);
        }
    }

    [Fact]
    public void TheWarning_NamesTheProperty_TheElement_AndAL0573()
    {
        var line = EmitOnce("65913", 1596877897, "Enabled", out var printed);

        Assert.True(printed, "the first call for a (page, element, property) must print");
        Assert.Contains("page 65913 element 1596877897", line);
        Assert.Contains("Enabled is bound to a procedure call", line);
        Assert.Contains("AL0573", line);
        // Not a bare "something is wrong": it says what the runner answered and why, so the
        // author can tell this apart from the property being false for an ordinary reason.
        Assert.Contains("answers false", line);
    }

    [Fact]
    public void TheWarning_SurvivesTheDefaultFilter_AndVerbose()
    {
        var line = EmitOnce("65913", 424242, "Enabled", out _).TrimEnd('\r', '\n');

        Assert.Contains(line, FilterOnce(line, verbose: false));
        Assert.Contains(line, FilterOnce(line, verbose: true));
    }

    [Fact]
    public void TheWarning_FiresOncePerElement_NotOncePerRead()
    {
        // A distinct element id per test method: the ledger is process-wide by design, so a
        // shared id would make the "once" assertion depend on test ordering.
        const int Element = 909090;

        var first = EmitOnce("65913", Element, "Enabled", out var printedFirst);
        var second = EmitOnce("65913", Element, "Enabled", out var printedSecond);

        Assert.True(printedFirst);
        Assert.False(printedSecond);
        Assert.Contains("AL0573", first);
        Assert.Equal(string.Empty, second);

        // A DIFFERENT element on the same page still gets its own warning — the ledger is not a
        // per-page latch that hides the second offending action on a page with two.
        var otherElement = EmitOnce("65913", Element + 1, "Enabled", out var printedOther);
        Assert.True(printedOther);
        Assert.Contains($"element {Element + 1}", otherElement);

        // ...and so does the same element under a different property.
        var otherProperty = EmitOnce("65913", Element, "Visible", out var printedProperty);
        Assert.True(printedProperty);
        Assert.Contains("Visible is bound to a procedure call", otherProperty);
    }

    /// <summary>
    /// The discriminator the whole fix rests on, asserted against the production source rather
    /// than restated: an ABSENT property must keep answering true, and only an EMPTY string —
    /// the shape the compiler writes when it dropped a client expression — may take the new path.
    /// Collapsing the two back into one `IsNullOrEmpty` check would disable every action whose
    /// page metadata omits the property.
    /// </summary>
    [Fact]
    public void EvaluateProperty_KeepsNullAndEmptyApart()
    {
        var source = File.ReadAllText(
            Path.Combine(RepoRoot, "AlRunner", "Patches", "RunnerPageInstance.cs"));
        var body = source[source.IndexOf("private bool EvaluateProperty(", StringComparison.Ordinal)..];
        body = body[..body.IndexOf("\n    /// <summary>", StringComparison.Ordinal)];

        Assert.Matches(new Regex(@"if \(raw is null\) return true;"), body);
        Assert.Matches(new Regex(@"if \(raw\.Length == 0\) return ClientExpressionTheCompilerDropped"), body);
        Assert.DoesNotContain("IsNullOrEmpty(raw)", body);
    }
}
