// Tests for --fail-on-stub behavior (issue #1519).
// These tests run WITHOUT --fail-on-stub in the normal matrix, so they verify
// that stub calls and no-ops silently pass (existing behavior unchanged).
// The complementary tests that verify --fail-on-stub CAUSES failures live in
// AlRunner.Tests/FailOnStubTests.cs (C# pipeline tests).
codeunit 1321002 "Fail On Stub Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure RealHelper_ReturnsValue_WithoutFailOnStub()
    var
        Helper: Codeunit "FOS Real Helper";
        Result: Integer;
    begin
        // [GIVEN] A real (non-stub) codeunit compiled from source
        // [WHEN]  Its method is called
        Result := Helper.GetValue();
        // [THEN]  Returns the concrete value — not a default
        Assert.AreEqual(42, Result, 'Real helper must return 42');
    end;

    [Test]
    procedure RealHelper_VoidCall_DoesNotThrow_WithoutFailOnStub()
    var
        Helper: Codeunit "FOS Real Helper";
    begin
        // [GIVEN] A real (non-stub) codeunit
        // [WHEN]  Its void method is called
        Helper.DoWork();
        // [THEN]  No exception — real implementations are never blocked by the guard
        Assert.IsTrue(true, 'DoWork must not throw');
    end;

    [Test]
    procedure Commit_IsNoOp_WithoutFailOnStub()
    var
        Helper: Codeunit "FOS Real Helper";
    begin
        // [GIVEN] --fail-on-stub is NOT active (normal run)
        // [WHEN]  Commit() is called (via helper to keep the test codeunit itself simple)
        Helper.CallCommit();
        // [THEN]  No exception — Commit() is silently stripped in the runner
        Assert.IsTrue(true, 'Commit must not throw without --fail-on-stub');
    end;

    [Test]
    procedure Commit_Direct_IsNoOp_WithoutFailOnStub()
    begin
        // [GIVEN] --fail-on-stub is NOT active
        // [WHEN]  Commit() is called directly from the test codeunit
        Commit();
        // [THEN]  No exception
        Assert.IsTrue(true, 'Direct Commit must not throw without --fail-on-stub');
    end;
}
