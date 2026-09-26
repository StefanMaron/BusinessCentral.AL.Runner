// PageExtensionAdoptionTests — issue #4145, the ADOPTION half of the fix.
//
// The fix moves the pageextension binding ahead of SetSourceTable, because that is what raises
// the page's OnMetadataLoaded and therefore the only point at which an extension's controls can
// still register their source expressions. The constructor then ADOPTS the instances TryCreate
// already bound instead of binding again.
//
// The AL bundle tests/runner-extras/pageext-control-source-expression pins the *early bind*:
// removing it entirely reds the two extension-global arms with NavTestFieldNotFoundException.
// It cannot pin the adoption, and that is the gap this file closes. A second bind throws
// ArgumentException out of BC's RegisterPageExtension — which also writes a pageExtensionsById
// dictionary keyed on the extension's object number — and RegisterPageExtensionsOnTheForm
// deliberately catches that and downgrades it to a `[warn]`. So with the adoption branch removed
// the AL bundle still reports 4 passed / 0 failed and the only trace is a warning nothing reads.
// Measured before this file existed: forcing the constructor to bind a second time gave
// Tests: 4 total, all PASS, plus three unasserted `[warn]` lines.
//
// AL cannot assert this: the runner's own warning stream is not visible to AL (the same reason
// ProcedureBoundPropertyWarningTests exists). So the claim is pinned here, against the runner's
// stdout and against the production source.
using Xunit;

namespace AlRunner.Tests;

public sealed class PageExtensionAdoptionTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    private static string SourceOfRunnerPageInstance() => File.ReadAllText(
        Path.Combine(RepoRoot, "AlRunner", "Patches", "RunnerPageInstance.cs"));

    /// <summary>
    /// The adoption branch exists, and it adopts INSTEAD of binding — the `else` is what keeps a
    /// second RegisterPageExtension off the path when TryCreate already bound.
    ///
    /// <para>Asserted against the production source rather than restated, the same technique
    /// ProcedureBoundPropertyWarningTests.EvaluateProperty_KeepsNullAndEmptyApart uses. Turning
    /// the `else` into an unconditional call — the exact mutation that leaves the AL bundle at
    /// 4 passed — reds this.</para>
    /// </summary>
    [Fact]
    public void TheConstructor_AdoptsBoundExtensions_InsteadOfBindingThemAgain()
    {
        var source = SourceOfRunnerPageInstance();

        var ctor = source[source.IndexOf(
            "        Dictionary<int, object?>? boundExtensions)", StringComparison.Ordinal)..];
        ctor = ctor[..ctor.IndexOf("\n    /// <summary>", StringComparison.Ordinal)];

        // The adoption arm: copy what the caller bound.
        Assert.Contains("if (boundExtensions != null)", ctor);
        Assert.Contains("foreach (var kv in boundExtensions) _extensionInstances[kv.Key] = kv.Value;", ctor);

        // ...and the bind must be the ELSE, not a sibling statement. A second bind throws
        // ArgumentException, which RegisterPageExtensionsOnTheForm swallows into a [warn], so
        // without the else nothing fails and nothing visible changes.
        Assert.Contains("else", ctor);
        Assert.Equal(1, CountOccurrences(ctor, "RegisterPageExtensionsOnTheForm()"));

        var beforeElse = ctor[..ctor.IndexOf("else", StringComparison.Ordinal)];
        Assert.DoesNotContain("RegisterPageExtensionsOnTheForm()", beforeElse);
    }

    /// <summary>
    /// TryCreate binds BEFORE SetSourceTable. That ordering is the fix itself (#4145): the page's
    /// metadata load raises OnMetadataLoaded, where a pageextension's AL-emitted source-expression
    /// registrations live, so an extension bound after it registers nothing.
    /// </summary>
    [Fact]
    public void TryCreate_BindsTheExtensions_BeforeTheMetadataLoad()
    {
        var source = SourceOfRunnerPageInstance();

        var bind = source.IndexOf(
            "RegisterPageExtensionsOnTheForm(form, record, pageId, boundExtensions);",
            StringComparison.Ordinal);
        Assert.True(bind > 0, "TryCreate must bind the extensions itself, before the metadata load");

        var setSourceTable = source.IndexOf("SetSourceTable", bind, StringComparison.Ordinal);
        Assert.True(setSourceTable > bind,
            "the bind must precede SetSourceTable — that call is what raises OnMetadataLoaded, "
            + "and an extension bound after it registers no source expressions (#4145)");
    }

    /// <summary>
    /// The bind-failure warning must not claim more than a failed bind actually costs.
    ///
    /// <para>It used to read "the page triggers it declares will not run", which is measurably
    /// wrong: <c>FindTrigger</c> walks each pageextension through
    /// <c>GetOrCreateExtensionInstance</c>, a path that never consults
    /// <c>NavForm.pageExtensions</c>, so an extension's control and action triggers still run
    /// after a failed <c>RegisterPageExtension</c>. What is genuinely lost is the
    /// <c>PageExtensions.ForEachAsync</c> pass each <c>NavForm.RaiseOn&lt;trigger&gt;Async</c>
    /// ends with — the page-level triggers.</para>
    /// </summary>
    [Fact]
    public void TheBindFailureWarning_NamesOnlyThePageLevelTriggers()
    {
        var source = SourceOfRunnerPageInstance();

        var start = source.IndexOf("not bind it to the page", StringComparison.Ordinal);
        Assert.True(start > 0, "the bind-failure warning must still exist");
        // To the end of the WriteLine statement. Not the first ");" — that one sits inside the
        // interpolated {inner.GetType()} and would cut the message off before its tail.
        var warn = source[start..];
        warn = warn[..warn.IndexOf("\");\n", StringComparison.Ordinal)];

        Assert.Contains("page-level triggers", warn);
        Assert.Contains("OnOpenPage", warn);

        // The overstatement this test exists to keep out. A control or action trigger the
        // extension declares DOES still run, so a message promising otherwise sends the reader
        // looking for a second defect that is not there.
        Assert.DoesNotContain("triggers it declares will not run", warn);
    }

    /// <summary>
    /// #4738: a page the runner builds for AL (Page.Run / RunModal, a Page variable) binds its
    /// extensions before its own SetSourceTable, and the constructor ADOPTS those instead of
    /// binding again. The AL side is corpus codeunit 67470 "PXR Tests"; this pins the adoption,
    /// whose loss AL sees only through an extension control trigger running on a second instance.
    /// </summary>
    [Fact]
    public void TheConstructor_AdoptsExtensionsBoundBeforeTheMetadataLoad_InsteadOfBindingAgain()
    {
        var source = SourceOfRunnerPageInstance();
        var ctor = source[source.IndexOf(
            "        Dictionary<int, object?>? boundExtensions)", StringComparison.Ordinal)..];
        ctor = ctor[..ctor.IndexOf("\n    /// <summary>", StringComparison.Ordinal)];

        var adopt = ctor.IndexOf("else if (PreBoundExtensions.TryGetValue(form, out var preBound))",
            StringComparison.Ordinal);
        var bind = ctor.IndexOf("RegisterPageExtensionsOnTheForm()", StringComparison.Ordinal);
        Assert.True(adopt > 0, "the constructor must adopt extensions a construction site pre-bound");
        Assert.True(bind > adopt, "the bind must be the fall-through AFTER the pre-bound adoption");
        Assert.Contains("foreach (var kv in preBound) _extensionInstances[kv.Key] = kv.Value;", ctor);
    }

    [Theory]
    [InlineData("FormStaticRunModalPatches.cs", "RunnerPageInstance.BindPageExtensionsBeforeMetadataLoad(instance, boundRecord, id);")]
    [InlineData("CodeunitPatches.cs", "RunnerPageInstance.BindPageExtensionsBeforeMetadataLoad(instance, record, id);")]
    public void PagesBuiltForAl_BindTheirExtensions_BeforeSetSourceTable(string file, string bindCall)
    {
        var source = File.ReadAllText(Path.Combine(RepoRoot, "AlRunner", "Patches", file));
        var bind = source.IndexOf(bindCall, StringComparison.Ordinal);
        Assert.True(bind > 0, $"{file} must bind the page's extensions before its metadata load (#4738)");
        Assert.Equal(1, CountOccurrences(source, bindCall));

        var setSourceTable = source.IndexOf("\"SetSourceTable\"", bind, StringComparison.Ordinal);
        var invoke = source.IndexOf("setSourceTable?.Invoke(", bind, StringComparison.Ordinal);
        Assert.True(setSourceTable > bind && invoke > setSourceTable,
            "the bind must precede SetSourceTable, which raises OnMetadataLoaded (#4738)");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
