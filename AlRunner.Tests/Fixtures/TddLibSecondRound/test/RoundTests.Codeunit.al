/// <summary>
/// #5264: "RaiseZ_ReachesTheStubThroughThreeRoundsOfSubscribers" raises an event whose subscriber raises
/// another one, and so on for three rounds; "Quiet_IsNotAnnotated" raises an event nobody subscribes to.
/// </summary>
codeunit 72220 "Round Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure RaiseZ_ReachesTheStubThroughThreeRoundsOfSubscribers()
    var
        Publisher: Codeunit "Round Publisher";
    begin
        Publisher.RaiseZ();
    end;

    [Test]
    procedure RaiseZ2_ReachesTheStubInTheAppThroughThreeRoundsInTheTestBundle()
    var
        Publisher: Codeunit "Round Publisher";
    begin
        Publisher.RaiseZ2();
    end;

    [Test]
    procedure Quiet_IsNotAnnotated()
    var
        Publisher: Codeunit "Round Publisher";
    begin
        Publisher.Quiet();
    end;
}
