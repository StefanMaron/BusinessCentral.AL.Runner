// This test codeunit contains DELIBERATELY BROKEN tests.
// It exists to demonstrate AL Runner's error output format.
// See the samples-fail.yml workflow for how this is used in CI.
codeunit 50906 "Greeter Tests (Broken)"
{
    Subtype = Test;

    var
        Greeter: Codeunit "Greeter";
        Assert: Codeunit Assert;

    [Test]
    procedure TestGreet_WrongExpectedValue()
    var
        Result: Text[250];
    begin
        // DELIBERATELY BROKEN: expects wrong value to demonstrate Assert.AreEqual output
        Result := Greeter.Greet('World');
        Assert.AreEqual('Goodbye, World!', Result, 'Greeting should match');
    end;

    [Test]
    procedure TestGreet_EmptyName_WrongError()
    begin
        // DELIBERATELY BROKEN: expects wrong error message to demonstrate Assert.ExpectedError output
        asserterror Greeter.Greet('');
        Assert.ExpectedError('Please provide a name');
    end;
}
