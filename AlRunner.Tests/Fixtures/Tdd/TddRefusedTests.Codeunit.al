/// <summary>
/// The two "must refuse" cases from #1997/#2001 (carried verbatim from the issue text):
/// a bare-statement procedure call (no way to tell void from a discarded return value), and
/// an assignment where BOTH sides are unresolved (nothing on either side anchors a type).
/// Neither is generated — both fall through to TddSupport's pre-existing refuse path
/// (excluded, reported FAILED naming the AL diagnostic), proving generation does not invent
/// a guess just because SOME anchor exists elsewhere in the object.
/// </summary>
codeunit 65015 "Tdd Refused Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure BareStatementCall_RefusesNotGuesses()
    var
        Target: Codeunit "Tdd Target Cu";
    begin
        Target.DoThing();
    end;

    [Test]
    procedure BothSidesUnresolved_RefusesNotGuesses()
    var
        Rec: Record "Tdd Target Table";
    begin
        Rec."Bar" := GetUnknownValue();
    end;
}
