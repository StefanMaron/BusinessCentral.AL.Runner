codeunit 58302 "ILV Interface Local Var Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;

    // -----------------------------------------------------------------------
    // Local interface variable — declare, assign, dispatch
    // -----------------------------------------------------------------------

    [Test]
    procedure LocalInterfaceVar_AssignCodunit_DispatchesCorrectly()
    var
        Greeter: Interface "ILV Greeter";
        Hello: Codeunit "ILV Hello Greeter";
    begin
        // [GIVEN] A local interface variable assigned from a codeunit
        Greeter := Hello;
        // [WHEN] Dispatch via the interface variable
        // [THEN] Returns the expected text from the codeunit implementation
        Assert.AreEqual('Hello', Greeter.Greet(), 'Dispatch through local interface var must call the assigned codeunit');
    end;

    [Test]
    procedure LocalInterfaceVar_AssignDifferentImpl_DispatchesCorrectImpl()
    var
        Greeter: Interface "ILV Greeter";
        Goodbye: Codeunit "ILV Goodbye Greeter";
    begin
        // [GIVEN] A local interface variable assigned from a different codeunit
        Greeter := Goodbye;
        // [WHEN] Dispatch via the interface variable
        // [THEN] Returns the value from the second codeunit, not the first
        Assert.AreEqual('Goodbye', Greeter.Greet(), 'Dispatch must use the assigned implementation, not a fixed one');
    end;

    [Test]
    procedure LocalInterfaceVar_MethodWithParam_PassesParamCorrectly()
    var
        Greeter: Interface "ILV Greeter";
        Hello: Codeunit "ILV Hello Greeter";
    begin
        // [GIVEN] A local interface variable
        Greeter := Hello;
        // [WHEN] Method with a parameter is called through the interface
        // [THEN] Parameter is forwarded correctly
        Assert.AreEqual('Hello World', Greeter.GreetName('World'), 'Parameter must be passed through interface dispatch');
    end;

    [Test]
    procedure LocalInterfaceVar_Reassigned_DispatchesNewImpl()
    var
        Greeter: Interface "ILV Greeter";
        Hello: Codeunit "ILV Hello Greeter";
        Goodbye: Codeunit "ILV Goodbye Greeter";
    begin
        // [GIVEN] A local interface variable assigned to one implementation
        Greeter := Hello;
        Assert.AreEqual('Hello', Greeter.Greet(), 'First assignment must dispatch to Hello');
        // [WHEN] Reassigned to a different codeunit
        Greeter := Goodbye;
        // [THEN] Dispatch uses the new implementation
        Assert.AreEqual('Goodbye', Greeter.Greet(), 'After reassignment, dispatch must use the new implementation');
    end;

    [Test]
    procedure TwoLocalInterfaceVars_IndependentDispatch()
    var
        Greeter1: Interface "ILV Greeter";
        Greeter2: Interface "ILV Greeter";
        Hello: Codeunit "ILV Hello Greeter";
        Goodbye: Codeunit "ILV Goodbye Greeter";
    begin
        // [GIVEN] Two independent local interface variables
        Greeter1 := Hello;
        Greeter2 := Goodbye;
        // [WHEN] Both are dispatched
        // [THEN] Each dispatches to its own assigned codeunit
        Assert.AreEqual('Hello', Greeter1.Greet(), 'Greeter1 must dispatch to Hello');
        Assert.AreEqual('Goodbye', Greeter2.Greet(), 'Greeter2 must dispatch to Goodbye');
    end;
}
