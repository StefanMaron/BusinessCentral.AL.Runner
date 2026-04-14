codeunit 53500 "Test Validate No Value"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ValidateWithoutValueReTriggersOnValidate()
    var
        Rec: Record "Val No Val Table";
    begin
        // Positive: Validate(FieldNo) without value re-fires OnValidate on current value
        Rec.Init();
        Rec."No." := 'V001';
        Rec."Price" := 50;
        // Now re-validate to fire the trigger on the current value
        Rec.Validate(Price);
        Assert.AreEqual(100, Rec.Computed, 'Computed should be 50 * 2 = 100');
    end;

    [Test]
    procedure ValidateWithValueSetsAndTriggers()
    var
        Rec: Record "Val No Val Table";
    begin
        // Positive: Validate(FieldNo, Value) sets AND triggers
        Rec.Init();
        Rec."No." := 'V002';
        Rec.Validate(Price, 25);
        Assert.AreEqual(50, Rec.Computed, 'Computed should be 25 * 2 = 50');
    end;

    [Test]
    procedure ValidateNoValueOnZeroDoesNothing()
    var
        Rec: Record "Val No Val Table";
    begin
        // Negative: Validate(FieldNo) on zero price doesn't set Computed
        Rec.Init();
        Rec."No." := 'V003';
        Rec.Validate(Price);
        Assert.AreEqual(0, Rec.Computed, 'Computed should remain 0 for zero price');
    end;
}
