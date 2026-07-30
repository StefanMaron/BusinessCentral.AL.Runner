// Reproduces the Pageworks 212-test cluster: element access on a
// List of [Codeunit] (SharedNavObjectList<NavCodeunitHandle>.Get) threw on the
// skeleton runtime — surfaced (masked) as "Value cannot be null." at
// PageworksLayoutCore.BuildChildNodeList.
//
// RED (before the fix): L.Get(1, C) throws.
// GREEN (after the fix): the codeunit instance added in a CALLEE scope is
// retrieved in the caller scope with its instance state intact.
codeunit 63702 "LCS Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "LCS Assert";

    local procedure BuildList(var Result: List of [Codeunit "LCS Stateful"])
    var
        C: Codeunit "LCS Stateful";
    begin
        C.SetValue(42);
        Result.Add(C);
    end;

    // The Pageworks shape: list populated in a callee, consumed in the caller.
    [Test]
    procedure CalleeScopeAdd_CallerScopeGet_StateRoundTrips()
    var
        L: List of [Codeunit "LCS Stateful"];
        C: Codeunit "LCS Stateful";
    begin
        BuildList(L);
        Assert.AreEqual(1, L.Count(), 'List built in callee scope must have 1 element');
        L.Get(1, C);
        Assert.AreEqual(42, C.GetValue(), 'Codeunit state set in callee scope must round-trip through the list');
    end;

    // Discriminator: Add + Get within ONE scope.
    [Test]
    procedure SameScopeAddGet_StateRoundTrips()
    var
        L: List of [Codeunit "LCS Stateful"];
        C: Codeunit "LCS Stateful";
        C2: Codeunit "LCS Stateful";
    begin
        C.SetValue(7);
        L.Add(C);
        L.Get(1, C2);
        Assert.AreEqual(7, C2.GetValue(), 'Codeunit state must round-trip through same-scope Add+Get');
    end;
}
