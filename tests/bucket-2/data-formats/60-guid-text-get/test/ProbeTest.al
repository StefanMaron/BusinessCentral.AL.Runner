codeunit 56602 "GRT Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure GetByGuidRoundTrippedThroughText()
    var
        Row: Record "GRT Row";
        Row2: Record "GRT Row";
        Alert: Record "GRT Alert";
    begin
        Row."Package ID" := '22222222-2222-2222-2222-222222222222';
        Row.Name := 'X';
        Row.Insert();

        // Direct Guid -> Get works.
        Assert.IsTrue(Row2.Get(Row."Package ID"), 'Direct Get by Guid value should succeed');

        // Guid -> Text[100] -> Get (the text round-trip path).
        Alert.Id := 1;
        Alert.UniqueIdentifier := Row."Package ID";
        Alert.Insert();

        Assert.IsTrue(Row2.Get(Alert.UniqueIdentifier), 'Get via Text[100]-roundtripped Guid should also succeed');
    end;
}
