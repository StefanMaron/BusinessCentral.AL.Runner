codeunit 58001 "Fluent Builder Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure ChainedSettersRetainValues()
    var
        Builder: Codeunit "Fluent Builder";
    begin
        // Positive: fluent chaining via exit(this) should retain all values
        Builder.SetValue(42).SetName('Hello');
        Assert.AreEqual(42, Builder.GetValue(), 'Value should be 42 after chaining');
        Assert.AreEqual('Hello', Builder.GetName(), 'Name should be Hello after chaining');
    end;

    [Test]
    procedure SingleSetterReturnsSelf()
    var
        Builder: Codeunit "Fluent Builder";
        Result: Codeunit "Fluent Builder";
    begin
        // Positive: single exit(this) returns the same codeunit
        Result := Builder.SetValue(99);
        Assert.AreEqual(99, Result.GetValue(), 'Returned codeunit should have value 99');
    end;

    [Test]
    procedure DefaultValuesBeforeChaining()
    var
        Builder: Codeunit "Fluent Builder";
    begin
        // Negative: before setting, values should be defaults
        Assert.AreEqual(0, Builder.GetValue(), 'Default value should be 0');
        Assert.AreEqual('', Builder.GetName(), 'Default name should be empty');
    end;
}
