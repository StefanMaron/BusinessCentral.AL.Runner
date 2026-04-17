codeunit 84701 "CE Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;
        Src: Codeunit "CE Src";

    // ── Source-side [ErrorBehavior(Collect)] (baseline — already proven) ────────

    [Test]
    procedure SourceSideCollect_CollectsError()
    begin
        // Positive: [ErrorBehavior(Collect)] on a source method collects the error.
        Src.RaiseOneInCollectContext('hello');
        Assert.IsTrue(HasCollectedErrors(), 'Error must be collected after source-side Collect call');
        ClearCollectedErrors();
    end;

    [Test]
    procedure SourceSideCollect_MessagePreserved()
    begin
        // Positive: collected error message is preserved.
        Src.RaiseOneInCollectContext('specific-value');
        Assert.AreEqual('specific-value', Src.GetFirstMessage(), 'Collected error message must equal set value');
        ClearCollectedErrors();
    end;

    // ── Test-side [ErrorBehavior(Collect)] ─────────────────────────────────────

    [Test]
    [ErrorBehavior(ErrorBehavior::Collect)]
    procedure TestSideCollect_CollectsTwoErrors()
    begin
        // Positive: [ErrorBehavior(Collect)] on the TEST procedure activates collecting mode
        // so that collectible errors from called code are collected, not thrown.
        Src.RaiseTwoErrors();
        Assert.AreEqual(2, Src.CountCollectedErrors(), 'Both collectible errors must be collected');
    end;

    [Test]
    [ErrorBehavior(ErrorBehavior::Collect)]
    procedure TestSideCollect_IsCollectingErrors()
    begin
        // Positive: IsCollectingErrors() must return true inside [ErrorBehavior(Collect)] test.
        Assert.IsTrue(Src.IsCollecting(), 'IsCollectingErrors must be true inside ErrorBehavior::Collect');
    end;

    [Test]
    [ErrorBehavior(ErrorBehavior::Collect)]
    procedure TestSideCollect_MessagePreserved()
    begin
        // Positive: first collected error has the correct message.
        Src.RaiseTwoErrors();
        Assert.AreEqual('First error', Src.GetFirstMessage(), 'First collected error message must be correct');
    end;

    // ── HasCollectedErrors / ClearCollectedErrors ──────────────────────────────

    [Test]
    procedure HasCollectedErrors_FalseByDefault()
    begin
        // Positive: no errors collected before any collection call.
        Assert.IsFalse(HasCollectedErrors(), 'HasCollectedErrors must be false before any collection');
    end;

    [Test]
    procedure ClearCollectedErrors_EmptiesCollection()
    begin
        // Positive: after ClearCollectedErrors the collection is empty.
        Src.RaiseOneInCollectContext('to be cleared');
        ClearCollectedErrors();
        Assert.IsFalse(HasCollectedErrors(), 'HasCollectedErrors must be false after ClearCollectedErrors');
    end;

    [Test]
    procedure IsCollectingErrors_FalseOutside()
    begin
        // Negative: IsCollectingErrors must be false outside any [ErrorBehavior(Collect)] scope.
        Assert.IsFalse(IsCollectingErrors(), 'IsCollectingErrors must be false outside collect scope');
    end;

    // ── GetCollectedErrors count ───────────────────────────────────────────────

    [Test]
    procedure GetCollectedErrors_ZeroWithoutCollection()
    begin
        // Positive: GetCollectedErrors returns empty list when nothing collected.
        Assert.AreEqual(0, Src.CountCollectedErrors(), 'CountCollectedErrors must be 0 before collection');
    end;
}
