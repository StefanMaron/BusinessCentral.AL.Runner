codeunit 59751 "SM Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "SM Src";

    [Test]
    procedure StrMenu_NoHandler_ReturnsCancel()
    var
        choice: Integer;
    begin
        // Positive: without a handler registered, StrMenu must return 0 (cancel)
        // standalone — there is no interactive UI and no default selection.
        choice := Src.Pick('Alpha,Beta,Gamma');
        Assert.AreEqual(0, choice, 'Unhandled StrMenu must return 0 (cancel) standalone');
    end;

    [Test]
    procedure StrMenu_EmptyOptions_NoOpReturnsZero()
    begin
        // Edge: empty options string must not throw; returns 0.
        Assert.AreEqual(0, Src.Pick(''),
            'StrMenu with empty options must return 0');
    end;

    [Test]
    procedure StrMenu_WithDefault_NoHandler_ReturnsDefault()
    var
        choice: Integer;
    begin
        // With a default specified (arg 2), an un-handled StrMenu should return
        // the default selection. Default button 2 means the second menu item.
        choice := Src.PickWithDefault('Alpha,Beta,Gamma', 2);
        Assert.AreEqual(2, choice, 'Unhandled StrMenu with defaultNo=2 must return 2');
    end;

    [Test]
    procedure StrMenu_DefaultZero_ReturnsCancel()
    begin
        // defaultNo=0 means no default → cancel (returns 0).
        Assert.AreEqual(0, Src.PickWithDefault('A,B,C', 0),
            'StrMenu with defaultNo=0 must return 0 (cancel)');
    end;

    [Test]
    procedure StrMenu_DefaultLast_ReturnsLast()
    begin
        // Default pointing at the last option (index 3 of 3) must return 3.
        Assert.AreEqual(3, Src.PickWithDefault('A,B,C', 3),
            'StrMenu with defaultNo=last must return the last index');
    end;

    [Test]
    procedure StrMenu_WithCaption_NoHandler_ReturnsDefault()
    begin
        // Three-arg overload (options, default, caption) must also work.
        Assert.AreEqual(1, Src.PickWithDefaultAndCaption('Yes,No', 1, 'Proceed?'),
            'StrMenu 3-arg overload with defaultNo=1 must return 1');
    end;

    [Test]
    procedure StrMenu_NotAlwaysFirst_NegativeTrap()
    begin
        // Negative: guard against a stub that always returns 1. Setting
        // defaultNo=3 must yield 3 (not 1).
        Assert.AreNotEqual(1, Src.PickWithDefault('A,B,C', 3),
            'StrMenu must honor defaultNo, not always return 1');
    end;
}
