/// <summary>
/// Regression proof: a codeunit obtained via an interface enum cast keeps its
/// instance var-record field alive for a later interface method call.
///
/// RED (before the fix): ALCompiler.ToInterface(NavOption) disposed the
/// implementing NavCodeunitHandle after wrapping its Target in the interface
/// handle. Disposing tore down the codeunit instance tree, including the
/// instance var-record field "Probe". GetProbedName() then dereferenced the
/// disposed Probe handle and threw NullReferenceException — exactly the
/// BaseApp Codeunit7035 (Price Source - Vendor).GetId 'vendor' NRE.
///
/// GREEN (after the fix): ToInterface no longer disposes the source handle;
/// the interface owns the live codeunit instance, so Probe survives and
/// GetProbedName() returns 'alive'.
/// </summary>
codeunit 60210 "Iface State Tests ICS"
{
    Subtype = Test;

    var
        Assert: Codeunit "Iface Assert ICS";

    [Test]
    procedure InterfaceMethod_ReadsInstanceVarRecordField_NoNre()
    var
        Provider: Interface "IState Provider ICS";
        Kind: Enum "State Kind ICS";
        Result: Text;
    begin
        // [GIVEN] an enum value whose implementation owns an instance var-record field
        Kind := Kind::Vendor;

        // [WHEN] the enum is cast to the interface (compiles to ToInterface(NavOption))
        Provider := Kind;

        // [THEN] calling an interface method that touches that field does not NRE
        Result := Provider.GetProbedName();

        // [THEN] it returns the concrete value written to the surviving record field
        Assert.AreEqual('alive', Result,
            'Interface impl instance var-record field must survive the ToInterface cast');
    end;

    [Test]
    procedure InterfaceMethod_WrongExpectation_Errors()
    var
        Provider: Interface "IState Provider ICS";
        Kind: Enum "State Kind ICS";
        Result: Text;
    begin
        // [GIVEN] the same interface dispatch
        Kind := Kind::Vendor;
        Provider := Kind;
        Result := Provider.GetProbedName();

        // [WHEN] asserting a deliberately wrong value
        // [THEN] the assertion fails with a specific message (proves the assert is real)
        asserterror Assert.AreEqual('disposed', Result, 'forced mismatch');
        Assert.ExpectedError('Assert.AreEqual failed. Expected:<disposed>. Actual:<alive>.', GetLastErrorText());
    end;
}
