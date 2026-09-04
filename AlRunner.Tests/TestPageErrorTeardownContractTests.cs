// TestPageErrorTeardownContractTests — pins the C# CONTRACT issue #2656's fix depends on,
// not "what BC does" (that's the job of the companion corpus PR,
// StefanMaron/BusinessCentral.AL.Language.Tests#142, which proves the AL-observable behavior
// against a real BC 28.4 service tier -- 5/5 tests green -- and adjudicated 8/8 legs on the
// pre-existing codeunit 60793 "Test Page BgTask Tests" test this fix also flips GREEN).
//
// Measured against a real BC service tier: an unhandled error raised inside a page's
// OnAfterGetRecord trigger, fired by a TestPage navigation call (GoToRecord, MoveNext, ...)
// on an already-open TestPage, tears the TestPage's underlying client session down. Every
// subsequent call on that same TestPage variable then raises BC's own
// "The TestPage is not open.", discarding the trigger's own error text. An unhandled error
// from OnValidate or OnAction does NOT do this -- both propagate their own text and leave the
// page open.
//
// There is no reflection surface that exercises MockTestPage's dispatch without a loaded BC
// runtime/session (it is constructed only from inside a live NavTestPage), so what's provable
// here is that the source carries the mechanism: Loaded() catches the trigger's exception,
// sets the teardown flag, and discards the original error in favor of BC's own message; and
// every other public entry point a torn-down TestPage could still reach (RequireRecord --
// covering Move*/GoToBookmark/FindRowFromTableFieldValues/SetFilter/GetFilter -- plus Close,
// GetField, GetAction, GetPart, GetBuiltInAction) refuses by the same mechanism instead of
// silently proceeding.
using System;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageErrorTeardownContractTests
{
    private static string MockTestPageSource()
    {
        var dir = AppContext.BaseDirectory;
        var repoRoot = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", ".."));
        var path = Path.Combine(repoRoot, "AlRunner", "Patches", "MockTestPage.cs");
        Assert.True(File.Exists(path), $"expected to find {path}");
        return File.ReadAllText(path);
    }

    [Fact]
    public void ExceptionMessage_IsBcsOwnNotOpenWording()
    {
        var source = MockTestPageSource();

        // Measured verbatim against a real BC 28.4 service tier via
        // StefanMaron/BusinessCentral.AL.Language.Tests#142's local container run.
        Assert.Contains("\"The TestPage is not open.\"", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Loaded_DiscardsTheTriggersOwnErrorAndTearsDown()
    {
        var source = MockTestPageSource();
        var start = source.IndexOf("private bool Loaded(bool found)", StringComparison.Ordinal);
        Assert.True(start >= 0, "could not locate Loaded(bool) in MockTestPage.cs");
        var body = source.Substring(start, Math.Min(2000, source.Length - start));

        // The trigger call must be wrapped in a try/catch that sets the teardown flag and
        // throws BC's own exception instead of letting the trigger's own error propagate.
        Assert.True(
            Regex.IsMatch(body, @"try\s*\{\s*_page\?\.RaiseOnAfterGetRecord\(\);\s*\}\s*(//[^\n]*\n\s*)*catch"),
            "Loaded() no longer wraps RaiseOnAfterGetRecord() in a try/catch -- this reintroduces " +
            "#2656's defect of propagating the trigger's own error text instead of tearing the " +
            "TestPage down.");
        // Only NavBaseException -- a RunnerOutOfScopeException (plain System.Exception, never
        // NavBaseException) or a genuine runner NRE must propagate unmodified, not be relabelled
        // as "The TestPage is not open." (.claude/rules/loud-failures.md).
        Assert.Contains("catch (NavBaseException ex)", body, StringComparison.Ordinal);
        Assert.Contains("_tornDown = true;", body, StringComparison.Ordinal);
        Assert.Contains("throw MakeTestPageNotOpenException(ex);", body, StringComparison.Ordinal);
    }

    [Fact]
    public void Loaded_DoesNotTearDownDuringTheInitialOpenTimePosition()
    {
        var source = MockTestPageSource();
        var start = source.IndexOf("private bool Loaded(bool found)", StringComparison.Ordinal);
        Assert.True(start >= 0, "could not locate Loaded(bool) in MockTestPage.cs");
        var body = source.Substring(start, Math.Min(2000, source.Length - start));

        // MarkOpened / RunnerTestClientSession.GetPage's own initial positioning call already
        // runs inside a blanket catch{} that swallows whatever this throws -- teardown must not
        // apply there, or a swallowed first-row failure would leave the page permanently
        // (and silently, from the AL test's point of view) unusable afterward.
        Assert.Contains("if (_suppressTeardownOnLoad) throw;", body, StringComparison.Ordinal);
        Assert.Contains("internal bool MoveFirstDuringOpen()", source, StringComparison.Ordinal);
    }

    [Theory]
    // Every other entry point a torn-down TestPage could still reach must refuse by the same
    // mechanism -- RequireRecord is the single choke point for Move*/GoToBookmark/
    // FindRowFromTableFieldValues/SetFilter/GetFilter/field reads; Close/GetField/GetAction/
    // GetPart/GetBuiltInAction do not route through RequireRecord and need their own guard.
    [InlineData("protected internal NavRecord RequireRecord(string what)")]
    [InlineData("public override void Close()")]
    [InlineData("public override ITestField GetField(int id)")]
    [InlineData("public override ITestAction GetAction(int actionId)")]
    [InlineData("public override ITestPart GetPart(int controlId)")]
    [InlineData("public override ITestAction GetBuiltInAction(FormResult formResult)")]
    public void EntryPoint_RefusesWhenTornDown(string signature)
    {
        var source = MockTestPageSource();
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, $"could not locate '{signature}' in MockTestPage.cs");
        var body = source.Substring(start, Math.Min(400, source.Length - start));

        Assert.True(
            body.Contains("_tornDown", StringComparison.Ordinal) &&
            body.Contains("MakeTestPageNotOpenException()", StringComparison.Ordinal),
            $"'{signature}' does not guard on _tornDown / MakeTestPageNotOpenException() -- a " +
            "torn-down TestPage would still answer this call instead of refusing it, unlike " +
            "real BC (#2656).");
    }

    [Fact]
    public void TornDown_IsDistinctFromOpened()
    {
        var source = MockTestPageSource();
        var start = source.IndexOf("private bool Loaded(bool found)", StringComparison.Ordinal);
        Assert.True(start >= 0, "could not locate Loaded(bool) in MockTestPage.cs");
        var body = source.Substring(start, Math.Min(2000, source.Length - start));

        // _opened must stay TRUE across a teardown -- real BC's Close() THROWS "not open"
        // after teardown rather than silently no-opping the way it would for a page that was
        // simply never opened (NavTestPageBase.Close() only forwards into this class when
        // IsOpened() is true). If teardown cleared _opened instead of a separate flag, Close()
        // would stop being dispatched here at all and the throw below would never run.
        Assert.DoesNotContain("_opened = false", body, StringComparison.Ordinal);
        Assert.Contains("private bool _tornDown;", source, StringComparison.Ordinal);
    }
}
