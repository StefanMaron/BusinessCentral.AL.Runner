/// Regression tests for issue #1501.
///
/// Before the fix, the auto-stub generator dedup'd overloads by (name, paramCount).
/// When "CreateHeader" had two 3-param overloads — one with Enum "Doc Type" and one
/// with Code[20] for the second parameter — only the first overload encountered was
/// emitted. If the Code[20] overload was seen first, the Enum overload was dropped,
/// causing a NavOption->NavCode cast error at the call site.
///
/// The fix changes the strategy: same-arity overloads that differ in a parameter type
/// are MERGED, with differing positions widened to Variant. Variant accepts both
/// NavOption (Enum callers) and NavCode (Code callers) without a cast error.
///
/// Note: These tests compile both callee overloads from source (not auto-stubbed),
/// exercising the runtime dispatch layer. The C# test AutoStubEnumParamTests covers
/// the actual stub generator path end-to-end when alc.exe is available.
codeunit 1315004 "Multi OL Enum Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        SalesLib: Codeunit "Multi OL Sales Lib";

    // ---- Positive cases -------------------------------------------------------

    [Test]
    procedure CreateHeader_EnumOverload_Order_StoresCorrectOrdinal()
    var
        DocHeader: Record "Multi OL Doc Header";
    begin
        // [GIVEN] An Enum literal for the Order value (ordinal 1)
        // [WHEN]  CreateHeader is called with the Enum-typed overload
        // [THEN]  Ordinal 1 is stored — proves the Enum overload was dispatched,
        //         not dropped in favour of the Code-typed overload
        DocHeader.Init();
        SalesLib.CreateHeader(DocHeader, "Multi OL Doc Type"::Order, 'C0001');
        Assert.AreEqual(1, SalesLib.GetDocTypeOrdinal(DocHeader),
            'Order enum (ordinal 1) must be stored via the Enum-typed CreateHeader overload');
        Assert.AreEqual('C0001', DocHeader."Customer No",
            'Customer No must be set from the Enum overload CustomerNo parameter');
    end;

    [Test]
    procedure CreateHeader_EnumOverload_Quote_StoresCorrectOrdinal()
    var
        DocHeader: Record "Multi OL Doc Header";
    begin
        // [GIVEN] An Enum literal for the Quote value (ordinal 2)
        // [WHEN]  CreateHeader is called with the Enum-typed overload
        // [THEN]  The ordinal 2 is stored
        DocHeader.Init();
        SalesLib.CreateHeader(DocHeader, "Multi OL Doc Type"::Quote, 'C0002');
        Assert.AreEqual(2, SalesLib.GetDocTypeOrdinal(DocHeader),
            'Quote enum (ordinal 2) must be stored via the Enum-typed CreateHeader overload');
    end;

    [Test]
    procedure CreateHeader_EnumOverload_Invoice_StoresOrdinal3()
    var
        DocHeader: Record "Multi OL Doc Header";
    begin
        // [GIVEN] An Enum literal for the Invoice value (ordinal 3)
        // [WHEN]  CreateHeader is called with the Enum-typed overload
        // [THEN]  Ordinal 3 is stored
        DocHeader.Init();
        SalesLib.CreateHeader(DocHeader, "Multi OL Doc Type"::Invoice, 'C0003');
        Assert.AreEqual(3, SalesLib.GetDocTypeOrdinal(DocHeader),
            'Invoice enum (ordinal 3) must be stored via the Enum-typed CreateHeader overload');
    end;

    // ---- Negative / edge cases ------------------------------------------------

    [Test]
    procedure CreateHeader_EnumOverload_DefaultEnum_StoresZeroOrdinal()
    var
        DocHeader: Record "Multi OL Doc Header";
        DefaultDocType: Enum "Multi OL Doc Type";
    begin
        // [GIVEN] The default enum value (ordinal 0)
        // [WHEN]  CreateHeader is called with the default enum variable
        // [THEN]  Ordinal 0 is stored and CustomerNo is set — proves dispatch reached the method
        DocHeader.Init();
        SalesLib.CreateHeader(DocHeader, DefaultDocType, 'C0000');
        Assert.AreEqual(0, SalesLib.GetDocTypeOrdinal(DocHeader),
            'Default enum (ordinal 0) must dispatch to the Enum overload without a cast error');
        Assert.AreEqual('C0000', DocHeader."Customer No",
            'Customer No must be set even for the default enum value');
    end;
}
