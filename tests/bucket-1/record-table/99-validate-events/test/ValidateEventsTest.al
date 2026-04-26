codeunit 59902 "VTE Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure ValidateFieldFiresBeforeAndAfterEvents()
    var
        Src: Record "VTE Source";
        C: Record "VTE Counter";
    begin
        // Positive: validating a field fires both OnBeforeValidate and OnAfterValidate
        Src.PK := 1;
        Src.Insert();

        Src.Validate(Amount, 100.0);

        Assert.IsTrue(C.Get(1), 'Counter should exist after validate events');
        Assert.AreEqual(1, C.BeforeCount, 'OnBeforeValidate should have fired once');
        Assert.AreEqual(1, C.AfterCount, 'OnAfterValidate should have fired once');
    end;

    [Test]
    procedure ValidateEventReceivesCorrectFieldNo()
    var
        Src: Record "VTE Source";
        C: Record "VTE Counter";
    begin
        // Positive: subscriber receives the correct CurrFieldNo
        Src.PK := 1;
        Src.Insert();

        Src.Validate(Amount, 50.0);

        Assert.IsTrue(C.Get(1), 'Counter should exist');
        Assert.AreEqual(2, C.LastFieldNo, 'CurrFieldNo should be 2 (Amount field)');
    end;

    [Test]
    procedure NoValidateEventWithoutValidateCall()
    var
        C: Record "VTE Counter";
    begin
        // Negative: no validate events fire without actual Validate call
        Assert.IsFalse(C.Get(1), 'No counter should exist without validate');
    end;
}
