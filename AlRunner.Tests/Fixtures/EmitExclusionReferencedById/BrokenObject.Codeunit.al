/// <summary>
/// The broken half: references a codeunit that exists nowhere, so it cannot bind and the
/// runner's emit-retry loop drops it. Same shape as Fixtures/EmitExclusion's broken object;
/// what differs is that a survivor here reaches it by object id.
/// </summary>
codeunit 60621 "Excl Ref Broken"
{
    Subtype = Test;

    [Test]
    procedure ExclRefBroken_NeverRuns()
    var
        Missing: Codeunit "This Codeunit Does Not Exist At All";
    begin
        Missing.DoSomething();
    end;
}
