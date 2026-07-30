/// <summary>
/// Cross-app regression proof: a var Rec mutation inside an OnAfterValidateEvent
/// subscriber that lives in a DIFFERENT app than the table must still propagate
/// to the record being validated. This is the dimension the single-app
/// reproducer cannot cover, and matches the reported RS pattern (ISV subscriber
/// on BaseApp "Purchase Header").
/// </summary>
codeunit 63310 "Mut Probe Tests XESM"
{
    Subtype = Test;

    var
        Assert: Codeunit "Mut Assert XESM";

    [Test]
    procedure Validate_CrossAppSubscriberMutation_Propagates()
    var
        Rec: Record "Mut Probe XESM";
    begin
        Rec.Init();
        Rec."No." := 'A1';
        Rec."Target Field" := '';

        Rec.Validate("Trigger Field", 'X');

        Assert.AreEqual('MUTATED:X', Rec."Target Field",
            'Cross-app OnAfterValidate subscriber mutation of var Rec must propagate to the validated record');
    end;
}
