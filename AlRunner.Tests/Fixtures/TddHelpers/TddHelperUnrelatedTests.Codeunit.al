/// <summary>Calls a helper that reaches no generated member: never annotated (#5147).</summary>
codeunit 65054 "Tdd Helper Unrelated Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    var
        Assert: Codeunit "Tdd Helper Assert";

    [Test]
    procedure ViaHelperWithoutStub_IsNotAnnotated()
    begin
        Assert.AreEqual(3, Sum(1, 2), 'a helper that reaches no stub');
    end;

    local procedure Sum(A: Integer; B: Integer): Integer
    begin
        exit(A + B);
    end;
}
