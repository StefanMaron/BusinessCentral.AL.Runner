codeunit 1320509 "CLN Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ListGet_NegatedMissing_ReturnsTrue()
    var
        list: List of [Codeunit "CLN Worker"];
        worker: Codeunit "CLN Worker";
    begin
        Assert.IsTrue(not list.Get(1, worker),
            'Negating List.Get should work for missing entries');
    end;

    [Test]
    procedure ListGet_NegatedPresent_ReturnsFalse()
    var
        list: List of [Codeunit "CLN Worker"];
        worker: Codeunit "CLN Worker";
    begin
        list.Add(worker);
        Assert.IsFalse(not list.Get(1, worker),
            'Negating List.Get should be false when the entry exists');
    end;
}
