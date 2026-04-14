codeunit 53200 "Test Interface Param"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    [Test]
    procedure CodeunitPassedAsInterfaceParam()
    var
        Greeter: Codeunit "Hello Greeter";
        Service: Codeunit "Greeting Service";
        Result: Text;
    begin
        // Positive: codeunit passed directly to interface-typed parameter
        Result := Service.MakeGreeting(Greeter, 'World');
        Assert.AreEqual('Hello World', Result, 'Should greet World');
    end;

    [Test]
    procedure CodeunitAssignedToInterfaceField()
    var
        Greeter: Codeunit "Hello Greeter";
        Runner: Codeunit "Greeting Runner";
        Result: Text;
    begin
        // Positive: codeunit assigned to interface field then used
        Runner.SetGreeter(Greeter);
        Result := Runner.RunGreeting('Field');
        Assert.AreEqual('Hello Field', Result, 'Should greet via interface field');
    end;

    [Test]
    procedure InterfaceParamGivesCorrectResult()
    var
        Greeter: Codeunit "Hello Greeter";
        Service: Codeunit "Greeting Service";
        Result: Text;
    begin
        // Negative: verify the interface actually runs, not just returns empty
        Result := Service.MakeGreeting(Greeter, 'AL');
        Assert.AreNotEqual('', Result, 'Result should not be empty');
        Assert.AreNotEqual('AL', Result, 'Result should not be just the name');
    end;
}
