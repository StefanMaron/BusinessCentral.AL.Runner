codeunit 1320511 "CLG Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ListGet_AssignsCodeunit()
    var
        list: List of [Codeunit "CLG Worker"];
        stored: Codeunit "CLG Worker";
        result: Codeunit "CLG Worker";
        value: Integer;
    begin
        list.Add(stored);
        if list.Get(1, result) then
            value := result.GetValue();
        Assert.AreEqual(99, value,
            'List.Get should assign the stored codeunit handle');
    end;

    [Test]
    procedure ListGet_Missing_ReturnsFalse()
    var
        list: List of [Codeunit "CLG Worker"];
        result: Codeunit "CLG Worker";
    begin
        Assert.IsFalse(list.Get(1, result),
            'List.Get should return false when the index is missing');
    end;
}
