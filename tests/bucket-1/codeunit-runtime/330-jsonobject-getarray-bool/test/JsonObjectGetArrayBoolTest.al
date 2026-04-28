codeunit 1320417 "JsonObject Bool Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "JsonObject Bool Src";

    [Test]
    procedure GetObject_RequireExists_ReturnsNested()
    begin
        Assert.IsTrue(Src.GetObjectRequireExists(),
            'GetObject(key, true) should return the nested object');
    end;

    [Test]
    procedure GetObject_RequireExists_MissingThrows()
    begin
        asserterror Src.GetObjectRequireExistsMissing();
        Assert.ExpectedError('does not contain a property with the name');
    end;

    [Test]
    procedure GetObject_Missing_ReturnsEmptyObject()
    begin
        Assert.IsFalse(Src.GetObjectMissingNoError(),
            'GetObject(key, false) should return an empty object for missing keys');
    end;

    [Test]
    procedure GetArray_RequireExists_ReturnsArray()
    begin
        Assert.AreEqual(2, Src.GetArrayRequireExists(),
            'GetArray(key, true) should return the array value');
    end;

    [Test]
    procedure GetArray_RequireExists_MissingThrows()
    begin
        asserterror Src.GetArrayRequireExistsMissing();
        Assert.ExpectedError('does not contain a property with the name');
    end;

    [Test]
    procedure GetArray_Missing_ReturnsEmptyArray()
    begin
        Assert.AreEqual(0, Src.GetArrayMissingNoError(),
            'GetArray(key, false) should return an empty array for missing keys');
    end;
}
