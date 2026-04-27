codeunit 1315002 "Format Option Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Helper: Codeunit "Format Option Helper";
        Style: Option Standard,Attention,Favorable;

    [Test]
    procedure Format_Option_AttentionRendersMemberName()
    var
        T: Text;
    begin
        // Attention is ordinal 1 — Format must return 'Attention', not '1'
        T := Helper.FormatStyle(Style::Attention);
        Assert.AreEqual('Attention', T, 'Format(Option::Attention) should render the member name');
    end;

    [Test]
    procedure Format_Option_StandardRendersMemberName()
    var
        T: Text;
    begin
        // Standard is ordinal 0 — Format must return 'Standard', not '0' or ''
        T := Helper.FormatStyle(Style::Standard);
        Assert.AreEqual('Standard', T, 'Format(Option::Standard) should render the member name even for zero ordinal');
    end;

    [Test]
    procedure Format_Option_FavorableRendersMemberName()
    var
        T: Text;
    begin
        T := Helper.FormatStyle(Style::Favorable);
        Assert.AreEqual('Favorable', T, 'Format(Option::Favorable) should render the member name');
    end;

    [Test]
    procedure Format_Option_NotEqualEmpty()
    var
        T: Text;
    begin
        T := Helper.FormatStyle(Style::Attention);
        Assert.IsTrue(T <> '', 'Format(Option) result must not be empty');
    end;

    [Test]
    procedure Format_Option_EqualLiteral()
    var
        Result: Boolean;
    begin
        Result := Helper.FormatStyleEqualsLiteral(Style::Attention, 'Attention');
        Assert.IsTrue(Result, 'Format(Option) compared against the correct literal must be true');
    end;

    [Test]
    procedure Format_Option_NotEqualWrongLiteral()
    var
        Result: Boolean;
    begin
        Result := Helper.FormatStyleEqualsLiteral(Style::Attention, 'Standard');
        Assert.IsFalse(Result, 'Format(Option) compared against wrong literal must be false');
    end;

    [Test]
    procedure Format_OptionLiteral_Direct()
    var
        T: Text;
    begin
        // Direct Format(Style::Attention) without a helper function
        T := Format(Style::Attention);
        Assert.AreEqual('Attention', T, 'Direct Format(Option::Member) must render member name');
    end;
}
