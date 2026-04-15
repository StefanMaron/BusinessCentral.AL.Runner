codeunit 55301 "Test Var Attributes"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ProtectedIntVariableAssignAndRead()
    var
        Helper: Codeunit "Var Attribute Helper";
    begin
        // [Protected] attribute is syntactic — assigned value must round-trip
        Helper.SetProtectedInt(42);
        Assert.AreEqual(42, Helper.GetProtectedInt(), '[Protected] Integer must store and return assigned value');
    end;

    [Test]
    procedure ProtectedTextVariableAssignAndRead()
    var
        Helper: Codeunit "Var Attribute Helper";
    begin
        Helper.SetProtectedText('Hello');
        Assert.AreEqual('Hello', Helper.GetProtectedText(), '[Protected] Text must store and return assigned value');
    end;

    [Test]
    procedure InternallyVisibleIntVariableAssignAndRead()
    var
        Helper: Codeunit "Var Attribute Helper";
    begin
        // [InternallyVisible] attribute is syntactic — assigned value must round-trip
        Helper.SetInternalInt(99);
        Assert.AreEqual(99, Helper.GetInternalInt(), '[InternallyVisible] Integer must store and return assigned value');
    end;

    [Test]
    procedure InternallyVisibleTextVariableAssignAndRead()
    var
        Helper: Codeunit "Var Attribute Helper";
    begin
        Helper.SetInternalText('World');
        Assert.AreEqual('World', Helper.GetInternalText(), '[InternallyVisible] Text must store and return assigned value');
    end;

    [Test]
    procedure ProtectedIntDefaultsToZero()
    var
        Helper: Codeunit "Var Attribute Helper";
    begin
        // [Protected] Integer without assignment must return 0 (type default)
        Assert.AreEqual(0, Helper.GetProtectedInt(), '[Protected] Integer default must be 0');
    end;

    [Test]
    procedure InternallyVisibleIntDefaultsToZero()
    var
        Helper: Codeunit "Var Attribute Helper";
    begin
        // [InternallyVisible] Integer without assignment must return 0 (type default)
        Assert.AreEqual(0, Helper.GetInternalInt(), '[InternallyVisible] Integer default must be 0');
    end;
}
