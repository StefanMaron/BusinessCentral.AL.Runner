codeunit 88001 "VI Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "VI Src";

    [Test]
    procedure IsJsonObject_True()
    begin
        Assert.IsTrue(Src.IsJsonObjectTrue(), 'Variant holding JsonObject must be IsJsonObject');
    end;

    [Test]
    procedure IsJsonObject_False_ForInteger()
    begin
        Assert.IsFalse(Src.IsJsonObjectFalse(), 'Variant holding Integer must not be IsJsonObject');
    end;

    [Test]
    procedure IsJsonObject_False_ForArray()
    begin
        Assert.IsFalse(Src.IsJsonObjectFalseForArray(), 'Variant holding JsonArray must not be IsJsonObject');
    end;

    [Test]
    procedure IsJsonArray_True()
    begin
        Assert.IsTrue(Src.IsJsonArrayTrue(), 'Variant holding JsonArray must be IsJsonArray');
    end;

    [Test]
    procedure IsJsonArray_False_ForObject()
    begin
        Assert.IsFalse(Src.IsJsonArrayFalseWhenObject(), 'Variant holding JsonObject must not be IsJsonArray');
    end;

    [Test]
    procedure IsJsonToken_True_ForObject()
    begin
        Assert.IsTrue(Src.IsJsonTokenTrueForObject(), 'Variant holding JsonObject must be IsJsonToken');
    end;

    [Test]
    procedure IsJsonToken_True_ForArray()
    begin
        Assert.IsTrue(Src.IsJsonTokenTrueForArray(), 'Variant holding JsonArray must be IsJsonToken');
    end;

    [Test]
    procedure IsJsonToken_False_ForInteger()
    begin
        Assert.IsFalse(Src.IsJsonTokenFalse(), 'Variant holding Integer must not be IsJsonToken');
    end;

    [Test]
    procedure IsJsonValue_True()
    begin
        Assert.IsTrue(Src.IsJsonValueTrue(), 'Variant holding JsonValue must be IsJsonValue');
    end;

    [Test]
    procedure IsJsonValue_False_ForObject()
    begin
        Assert.IsFalse(Src.IsJsonValueFalse(), 'Variant holding JsonObject must not be IsJsonValue');
    end;

    [Test]
    procedure IsNotification_True()
    begin
        Assert.IsTrue(Src.IsNotificationTrue(), 'Variant holding Notification must be IsNotification');
    end;

    [Test]
    procedure IsNotification_False_ForInteger()
    begin
        Assert.IsFalse(Src.IsNotificationFalse(), 'Variant holding Integer must not be IsNotification');
    end;

    [Test]
    procedure IsTextBuilder_True()
    begin
        Assert.IsTrue(Src.IsTextBuilderTrue(), 'Variant holding TextBuilder must be IsTextBuilder');
    end;

    [Test]
    procedure IsTextBuilder_False_ForInteger()
    begin
        Assert.IsFalse(Src.IsTextBuilderFalse(), 'Variant holding Integer must not be IsTextBuilder');
    end;

    [Test]
    procedure IsList_True()
    begin
        Assert.IsTrue(Src.IsListTrue(), 'Variant holding List must be IsList');
    end;

    [Test]
    procedure IsList_False_ForInteger()
    begin
        Assert.IsFalse(Src.IsListFalse(), 'Variant holding Integer must not be IsList');
    end;
}
