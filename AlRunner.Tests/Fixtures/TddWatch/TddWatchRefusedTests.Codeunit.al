/// <summary>
/// The "must refuse" case from #1997/#2001: a bare-statement procedure call gives no
/// way to tell a void procedure from a discarded return value, so --tdd's generation
/// refuses rather than guesses (TddGeneration.cs) — this object is excluded and its
/// [Test] procedure reported FAILED naming the AL diagnostic, exactly like #2000's
/// original refuse path. DoThing is deliberately NEVER implemented anywhere in this
/// fixture: this codeunit's exclusion — and therefore the whole module's
/// `excludedObjects.Count > 0` — is meant to be PERMANENT across every watch cycle,
/// which is what TddWatchTests.cs's watch-level test uses to prove the --tdd-specific
/// "no incremental baseline yet" fallback-reason text cleanly (see that file's doc
/// comment and issue #2009 for why the OTHER, generation-eligible test in this bundle
/// can't prove it on its own).
/// </summary>
codeunit 65102 "Tdd Watch Refused Tests"
{
    Subtype = Test;
    TestPermissions = Disabled;

    [Test]
    procedure BareStatementCall_RefusesNotGuesses()
    var
        Target: Codeunit "Tdd Watch Target Cu";
    begin
        Target.DoThing();
    end;
}
