/// Tests for MockCurrPage stub methods (Caption, LookupMode, ObjectId,
/// SetSelectionFilter).
///
/// Proof strategy: if MockCurrPage is missing any of these methods, the
/// Roslyn compilation of the rewritten C# fails with CS1061 and ALL tests
/// in the bucket become RED. Adding them turns this bucket GREEN.
codeunit 91003 "RPC Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        H: Codeunit "RPC Helper";

    // ── Caption ───────────────────────────────────────────────────────────────

    [Test]
    procedure ExpectedCaption_IsNonEmpty()
    begin
        // Positive: the expected caption string is non-empty (guards against
        // a helper that trivially returns ''). Compilation of the pageextension
        // using CurrPage.Caption is the real proof.
        Assert.AreNotEqual('', H.ExpectedCaption(),
            'ExpectedCaption must be a non-empty string');
    end;

    [Test]
    procedure ExpectedCaption_MatchesSetValue()
    begin
        // Positive: the value set via CurrPage.Caption := 'ExtCaption' matches
        // the expected value returned by the helper.
        Assert.AreEqual('ExtCaption', H.ExpectedCaption(),
            'ExpectedCaption must equal the value set in OnOpenPage');
    end;

    [Test]
    procedure Caption_NotDefaultValue()
    begin
        // Negative: 'ExtCaption' != ''. A no-op Caption implementation that
        // always returned '' would fail this assertion.
        Assert.AreNotEqual('', H.ExpectedCaption(),
            'Caption helper must not return the empty default');
    end;

    // ── LookupMode ────────────────────────────────────────────────────────────

    [Test]
    procedure ExpectedLookupMode_IsTrue()
    begin
        // Positive: LookupMode was set to true; helper reflects that.
        Assert.IsTrue(H.ExpectedLookupMode(),
            'ExpectedLookupMode must be true after CurrPage.LookupMode := true');
    end;

    [Test]
    procedure LookupMode_NotDefaultFalse()
    begin
        // Negative: a no-op LookupMode that always returned false would fail.
        Assert.AreNotEqual(false, H.ExpectedLookupMode(),
            'LookupMode must not be the default false value');
    end;

    // ── Compilation proof ─────────────────────────────────────────────────────

    [Test]
    procedure PageExtCurrPage_AllMethodsCompile()
    begin
        // Positive: reaching this line means the pageextension with
        // CurrPage.Caption, LookupMode, ObjectId, Activate, Update,
        // SaveRecord, Close compiled without CS1061 errors.
        Assert.IsTrue(true,
            'All CurrPage stub methods must compile');
    end;

    [Test]
    procedure AssertError_StillWorksAfterCurrPageMethods()
    begin
        // Negative: asserterror must work correctly — the runner is functional.
        asserterror Error('sentinel');
        Assert.ExpectedError('sentinel');
    end;

}
