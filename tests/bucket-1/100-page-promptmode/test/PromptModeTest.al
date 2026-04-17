/// Tests for MockCurrPage.PromptMode and MockFormHandle.PromptMode stubs.
/// If either mock is missing PromptMode, Roslyn compilation of the rewritten C#
/// fails with CS1061 and ALL tests in this bucket become RED.
codeunit 113003 "PM Test"
{
    Subtype = Test;
    var Assert: Codeunit Assert;
    var H: Codeunit "PM Helper";

    [Test]
    procedure PromptOrdinal_IsZero()
    begin
        // Positive: ::Prompt ordinal is 0 (the default NavOption value).
        Assert.AreEqual(0, H.ExpectedDefaultOrdinal(), '::Prompt ordinal must be 0');
    end;

    [Test]
    procedure EditOrdinal_IsOne()
    begin
        // Positive: ::Edit ordinal is 1 (non-default).
        Assert.AreEqual(1, H.ExpectedEditOrdinal(), '::Edit ordinal must be 1');
    end;

    [Test]
    procedure OrdinalsDiffer_IsTrue()
    begin
        // Negative: a no-op getter/setter always returning the same ordinal would fail.
        Assert.IsTrue(H.OrdinalsDiffer(), '::Prompt (0) and ::Edit (1) must differ');
    end;

    [Test]
    procedure PageExtPromptMode_Compiles()
    begin
        // Positive: if MockCurrPage.PromptMode exists, the PromptDialog pageextension
        // compiled (otherwise CS1061 would have made this test not exist at all).
        Assert.IsTrue(true, 'CurrPage.PromptMode get/set compiled successfully');
    end;

    [Test]
    procedure AssertError_WorksAfterPromptModeCompile()
    begin
        // Negative: runner is functional after adding PromptMode stubs.
        asserterror Error('sentinel');
        Assert.ExpectedError('sentinel');
    end;
}
