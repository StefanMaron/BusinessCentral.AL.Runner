// Reproduces the Pageworks PageworksCopilotSession gap: a SingleInstance=true
// codeunit must share ONE instance per session, so a value set through one
// codeunit variable/handle is visible through a DIFFERENT variable/handle of the
// SAME codeunit within the same test.
//
// RED (before the fix): NavCodeunitHandle_CreateTarget always constructs a fresh
// instance, so S2.GetValue() reads the default (0), not the 99 that S1.SetValue
// stored — this test fails.
// GREEN (after the fix): S2 resolves to the SAME cached instance as S1, so
// S2.GetValue() returns 99 — this test passes.
codeunit 61303 "SIC Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "SIC Assert";

    [Test]
    procedure SingleInstance_StateVisibleThroughDifferentVariable()
    var
        S1: Codeunit "SIC Single";
        S2: Codeunit "SIC Single";
    begin
        // S1 and S2 are two independent local codeunit variables of the SAME
        // SingleInstance=true codeunit — each resolves its own NavCodeunitHandle,
        // exercising a SEPARATE call to NavCodeunitHandle_CreateTarget.
        S1.SetValue(99);
        Assert.AreEqual(99, S2.GetValue(),
            'SingleInstance codeunit must share one instance per session — value set via S1 must be visible via S2');
    end;

    // Contrast case: an ordinary (SingleInstance=false) codeunit must NOT share
    // state across independent variables — this pins the fix to SingleInstance
    // codeunits only and proves the corpus-wide fresh-instance behavior for
    // regular codeunits is unchanged.
    [Test]
    procedure NonSingleInstance_FreshInstancePerVariable()
    var
        M1: Codeunit "SIC Multi";
        M2: Codeunit "SIC Multi";
    begin
        M1.SetValue(99);
        Assert.AreEqual(0, M2.GetValue(),
            'Non-SingleInstance codeunit must get a fresh instance per variable — M2 must see the default, not M1''s value');
    end;
}
