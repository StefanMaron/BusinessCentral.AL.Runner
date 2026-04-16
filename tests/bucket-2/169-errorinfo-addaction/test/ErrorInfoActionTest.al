codeunit 59891 "EIA Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "EIA Src";

    [Test]
    procedure AddAction_3Arg_NoOp()
    begin
        // Positive: 3-arg AddAction(caption, codeunitId, method) completes.
        Assert.IsTrue(Src.AddSingleAction('Fix it'),
            'AddAction 3-arg must complete without throwing');
    end;

    [Test]
    procedure AddAction_4Arg_WithDescription()
    begin
        // Positive: 4-arg AddAction(caption, codeunitId, method, description) completes.
        Assert.IsTrue(Src.AddSingleActionWithDescription('Fix it', 'Resolves the validation error'),
            'AddAction 4-arg must complete without throwing');
    end;

    [Test]
    procedure AddAction_EmptyCaption()
    begin
        // Edge: empty caption must not crash.
        Assert.IsTrue(Src.AddSingleAction(''),
            'AddAction with empty caption must not throw');
    end;

    [Test]
    procedure AddAction_EmptyDescription()
    begin
        // Edge: 4-arg with empty description must not crash.
        Assert.IsTrue(Src.AddSingleActionWithDescription('Fix', ''),
            'AddAction 4-arg with empty description must not throw');
    end;

    [Test]
    procedure AddAction_Multiple()
    begin
        // Positive: repeatedly adding actions must not crash the mock.
        Assert.IsTrue(Src.AddMultipleActions(),
            'Multiple AddAction calls must complete');
    end;

    [Test]
    procedure AddAction_LongStrings_NegativeTrap()
    begin
        // Negative trap: guard against a crash on large input.
        Assert.IsTrue(
            Src.AddSingleActionWithDescription(
                'A very long action caption that might stress a naive mock implementation',
                'A very long action description explaining every detail of how the fix works 0123456789_abcdefghij'),
            'AddAction with long strings must not throw');
    end;
}
