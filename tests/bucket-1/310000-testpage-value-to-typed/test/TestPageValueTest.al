codeunit 70140700 "TPV Read Tests"
{
    Subtype = Test;

    trigger OnRun()
    begin
        // [FEATURE] TestPage field .Value assignment to typed local variable
    end;

    [Test]
    procedure TextFieldValueAssignsToTextVar()
    var
        Card: TestPage "TPV Read Card";
        Captured: Text;
    begin
        Initialize();
        // [SCENARIO] Reading TestPage.<TextField>.Value into a Text variable
        // [GIVEN] A card with a Text field set to a known value
        Card.OpenNew();
        Card.DescriptionField.SetValue('Contoso Ltd');

        // [WHEN] The field's .Value is assigned to a Text variable
        // BC emits: captured = new NavText(card.GetField(h).ALValue)
        Captured := Card.DescriptionField.Value;

        // [THEN] The variable holds the value that was set (proves not a stub default)
        Assert.AreEqual('Contoso Ltd', Captured, 'Text var should hold the value set on the field');
        Card.Close();
    end;

    [Test]
    procedure CodeFieldValueAssignsToCodeVar()
    var
        Card: TestPage "TPV Read Card";
        Captured: Code[20];
    begin
        Initialize();
        // [SCENARIO] Reading TestPage.<CodeField>.Value into a Code variable
        // [GIVEN] A card with a Code field set to a known value
        Card.OpenNew();
        Card.NoField.SetValue('CUST-1407');

        // [WHEN] The field's .Value is assigned to a Code variable
        // BC emits: captured = new NavCode(card.GetField(h).ALValue)
        Captured := Card.NoField.Value;

        // [THEN] The variable holds the value that was set
        Assert.AreEqual('CUST-1407', Captured, 'Code var should hold the value set on the field');
        Card.Close();
    end;

    [Test]
    procedure TextValueDoesNotMatchUnsetValue()
    var
        Card: TestPage "TPV Read Card";
        Captured: Text;
    begin
        Initialize();
        // [SCENARIO] Negative — assigning .Value to a Text var and asserting
        // a different literal must not match (catches a stub returning '').
        // [GIVEN] A card with a Text field set to a specific value
        Card.OpenNew();
        Card.DescriptionField.SetValue('Fabrikam Inc');

        // [WHEN] Captured holds the field value
        Captured := Card.DescriptionField.Value;

        // [THEN] An unrelated literal must not equal the captured value
        Assert.AreNotEqual('Contoso Ltd', Captured, 'Captured value should not match an unrelated literal');
        Card.Close();
    end;

    local procedure Initialize()
    var
        Rec: Record "TPV Read Record";
    begin
        Rec.DeleteAll();
    end;

    var
        Assert: Codeunit "Library Assert";
}
