/// <summary>
/// Compiles perfectly, and still needs the dropped object at run time: `Codeunit.Run` takes
/// an Integer, so nothing binds the id 60621 to an object at compile time and the exclusion
/// does not cascade onto this file. Running this module would execute a call to a codeunit
/// that is not in it.
/// </summary>
codeunit 60611 "Excl Ref Healthy Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "Excl Ref Assert";

    [Test]
    procedure ExclRef_ReachesTheDroppedCodeunitById()
    begin
        Assert.AreEqual(3, 1 + 2, 'this part binds fine');
        if Codeunit.Run(60621) then;
    end;
}
