// Fixture suite for FeatureKeyVirtualTableTests.cs (#2585).
//
// The rows are BC's OWN — produced by FeatureKeyDataProvider, whose feature list is a
// hardcoded static in Microsoft.Dynamics.Nav.Types. So these assertions are about the ROUTING
// working, not about which features BC ships: naming a specific key here would pin a BC
// version, and which keys exist is what the corpus adjudicates across all eight legs.
codeunit 60821 "FKV Fixture Tests"
{
    Subtype = Test;
    var
        Assert: Codeunit "FKV Assert";

    [Test]
    procedure FeatureKey_AnswersBcsOwnRows()
    var
        FeatureKey: Record "Feature Key";
    begin
        // Before the fix this raised "There is no Feature Key within the filter."
        Assert.IsTrue(FeatureKey.FindSet(), 'Feature Key must answer at least one row.');
        Assert.IsTrue(FeatureKey.Count() > 1, 'BC ships more than one feature key.');
    end;

    [Test]
    procedure FeatureKey_EveryRowHasANonBlankIdThatGetRoundTrips()
    var
        FeatureKey: Record "Feature Key";
        Fetched: Record "Feature Key";
    begin
        // Rules out N blank rows, and proves Get reaches the same rowset FindSet walked.
        Assert.IsTrue(FeatureKey.FindSet(), 'Feature Key must answer at least one row.');
        repeat
            Assert.IsFalse(FeatureKey.ID = '', 'Every Feature Key row must carry a non-blank ID.');
            Assert.IsTrue(Fetched.Get(FeatureKey.ID), 'Get must find every ID that FindSet returned.');
            Assert.AreEqual(FeatureKey.ID, Fetched.ID, 'Get must return the row whose ID it was given.');
        until FeatureKey.Next() = 0;
    end;

    [Test]
    procedure FeatureKey_GetOnAnUnknownId_ReturnsFalse()
    var
        FeatureKey: Record "Feature Key";
    begin
        // Negative control: a provider answering every Get with a row would pass the above.
        Assert.IsFalse(FeatureKey.Get('ThisFeatureDoesNotExist'), 'Feature Key must not invent rows.');
    end;

    [Test]
    procedure FeatureKey_Modify_RefusesLoudlyInsteadOfSilentlyDoingNothing()
    var
        FeatureKey: Record "Feature Key";
    begin
        // The runner serves this table read-only. Real BC's Modify writes through to table
        // 2000000210; that path is not implemented, and our rows live in a temp store whose
        // Modify would ACCEPT the write and put it nowhere. This pins the loud refusal — a
        // silently-successful Modify is exactly what must not ship.
        Assert.IsTrue(FeatureKey.FindSet(), 'Feature Key must answer at least one row.');
        asserterror FeatureKey.Modify();
        if StrPos(GetLastErrorText(), 'feature-key-modify') = 0 then
            Error('Modify must refuse with the feature-key-modify reason, got: %1', GetLastErrorText());
    end;
}
