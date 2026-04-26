namespace My.App.Test;

using My.App;

codeunit 50451 "Namespace Thing Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ComputeWorksDespiteUnusedUsings()
    var
        Thing: Codeunit "Namespace Thing";
    begin
        // [GIVEN] A source file with `using` for unknown namespaces
        // [THEN] It should still compile and the codeunit must be callable
        Assert.AreEqual(5, Thing.Compute(2, 3), 'Compute should still work with unused usings');
    end;

    [Test]
    procedure NegativeSum()
    var
        Thing: Codeunit "Namespace Thing";
    begin
        Assert.AreNotEqual(10, Thing.Compute(2, 3), 'Sum of 2+3 is 5, not 10');
    end;
}
