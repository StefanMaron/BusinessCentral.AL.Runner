codeunit 81251 "EINA Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EINA Src";

    // -----------------------------------------------------------------------
    // Positive: AddNavigationAction is a no-op in standalone mode — both
    // overloads must complete without throwing.
    // -----------------------------------------------------------------------

    [Test]
    procedure AddNavigationAction_3Arg_NoOp()
    begin
        // Positive: 3-arg form (caption, pageId, filters) completes without error.
        Assert.IsTrue(Src.AddNavigationAction_3Arg('Open List'),
            'AddNavigationAction 3-arg must complete without throwing');
    end;

    [Test]
    procedure AddNavigationAction_4Arg_WithDescription()
    begin
        // Positive: 4-arg form (caption, pageId, filters, description) completes.
        Assert.IsTrue(Src.AddNavigationAction_4Arg('Open List', 'Navigate to related records'),
            'AddNavigationAction 4-arg must complete without throwing');
    end;

    [Test]
    procedure AddNavigationAction_PreservesMessage()
    begin
        // Positive: ErrorInfo.Message is not disturbed by AddNavigationAction.
        Assert.AreEqual('Record not found', Src.AddNavigationAction_WithMessage(),
            'ErrorInfo.Message must be preserved after AddNavigationAction call');
    end;

    [Test]
    procedure AddNavigationAction_Multiple_NoOp()
    begin
        // Positive: multiple AddNavigationAction calls must not crash.
        Assert.IsTrue(Src.AddMultipleNavigationActions(),
            'Multiple AddNavigationAction calls must complete');
    end;

    // -----------------------------------------------------------------------
    // Negative: empty caption and filter strings are accepted without error.
    // -----------------------------------------------------------------------

    [Test]
    procedure AddNavigationAction_EmptyCaption_Accepted()
    begin
        // Negative: empty caption must not throw (no-op accepts any input).
        Assert.IsTrue(Src.AddNavigationAction_3Arg(''),
            'AddNavigationAction with empty caption must not throw');
    end;

    [Test]
    procedure AddNavigationAction_EmptyDescription_Accepted()
    begin
        // Negative: empty description in 4-arg form must not throw.
        Assert.IsTrue(Src.AddNavigationAction_4Arg('Open', ''),
            'AddNavigationAction 4-arg with empty description must not throw');
    end;
}
