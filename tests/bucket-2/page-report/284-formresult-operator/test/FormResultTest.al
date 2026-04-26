codeunit 132002 "FRO Test"
{
    Subtype = Test;
    var
        Assert: Codeunit Assert;

    [Test]
    procedure BoolAndFormResult_BothTrue_ReturnsTrue()
    var
        Src: Codeunit "FRO Source";
    begin
        // [GIVEN] bool=true AND (Action=Action::OK) → true AND true
        Assert.IsTrue(Src.BoolAndFormResult_True(), 'true and (Action=Action::OK) must be true');
    end;

    [Test]
    procedure BoolAndFormResult_ActionMismatch_ReturnsFalse()
    var
        Src: Codeunit "FRO Source";
    begin
        // [GIVEN] bool=true AND (Action::Cancel=Action::OK) → true AND false
        Assert.IsFalse(Src.BoolAndFormResult_False(), 'true and (Cancel=OK) must be false');
    end;

    [Test]
    procedure BoolOrFormResult_ActionTrue_ReturnsTrue()
    var
        Src: Codeunit "FRO Source";
    begin
        // [GIVEN] bool=false OR (Action=Action::OK) → false OR true
        Assert.IsTrue(Src.BoolOrFormResult_True(), 'false or (Action=Action::OK) must be true');
    end;

    [Test]
    procedure FormResultAsAction_ReturnsLookupOK()
    var
        Src: Codeunit "FRO Source";
    begin
        // [GIVEN] Action::LookupOK assigned and returned
        Assert.AreEqual(Action::LookupOK, Src.FormResultAsAction_Compiles(), 'Action::LookupOK must round-trip');
    end;

    // ── FilterPageBuilder.RunModal() in compound boolean ────────────────────────
    // FilterPageBuilder.RunModal() returns Boolean in AL.
    // BC emits: cond & fPB.ALRunModal(DataError.TrapError)
    // MockFilterPageBuilder.ALRunModal() must return bool to avoid CS0019.

    [Test]
    procedure FilterBuilderAndBool_CondTrue_ReturnsTrue()
    var
        Src: Codeunit "FRO Source";
    begin
        // [GIVEN] true and FilterPageBuilder.RunModal() — RunModal always true in standalone
        // [THEN] true and true = true
        Assert.IsTrue(Src.FilterBuilderAndBool_True(true), 'true and FPB.RunModal() must be true');
    end;

    [Test]
    procedure FilterBuilderAndBool_CondFalse_ReturnsFalse()
    var
        Src: Codeunit "FRO Source";
    begin
        // [GIVEN] false and FilterPageBuilder.RunModal() — short-circuits to false
        // [THEN] false and anything = false
        Assert.IsFalse(Src.FilterBuilderAndBool_CondFalse(), 'false and FPB.RunModal() must be false');
    end;

    [Test]
    procedure FilterBuilderOrBool_RunModalTrue_ReturnsTrue()
    var
        Src: Codeunit "FRO Source";
    begin
        // [GIVEN] false or FilterPageBuilder.RunModal() — RunModal returns true in standalone
        // [THEN] false or true = true
        Assert.IsTrue(Src.FilterBuilderOrBool_True(), 'false or FPB.RunModal() must be true');
    end;
}
