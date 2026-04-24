codeunit 304001 "RecRef Factory Test"
{
    Subtype = Test;

    [Test]
    procedure RecRefArray_FactoryCompiles()
    var
        Src: Codeunit "RecRef Factory Src";
        Result: Integer;
    begin
        // Positive: array of RecordRef compiles and works correctly
        Result := Src.GetRecRefFromArray();
        Assert.AreEqual(2, Result, 'RecRefs[2].Number should be 2 after Open(2)');
    end;

    [Test]
    procedure RecRefArray_ElementsAreIndependent()
    var
        RecRefs: array[2] of RecordRef;
    begin
        // Positive: each element in a RecordRef array is independent
        RecRefs[1].Open(10);
        RecRefs[2].Open(20);
        Assert.AreEqual(10, RecRefs[1].Number, 'RecRefs[1] should have table 10');
        Assert.AreEqual(20, RecRefs[2].Number, 'RecRefs[2] should have table 20');
    end;

    var
        Assert: Codeunit "Library Assert";
}
