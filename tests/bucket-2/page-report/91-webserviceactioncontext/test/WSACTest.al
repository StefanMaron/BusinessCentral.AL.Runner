codeunit 95101 "WSAC Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "WSAC Src";

    // ==================================================================
    // ObjectId — round-trip get/set (Integer type — comparable in all BC versions)
    // ==================================================================

    [Test]
    procedure SetObjectId_GetObjectId_RoundTrip()
    var
        Ctx: WebServiceActionContext;
    begin
        // [GIVEN] A WebServiceActionContext
        // [WHEN]  SetObjectId(42) is called
        // [THEN]  GetObjectId() returns 42
        Assert.AreEqual(42, Src.SetObjectIdAndGet(Ctx, 42), 'ObjectId should round-trip to 42');
    end;

    [Test]
    procedure SetObjectId_DifferentValues_Distinct()
    var
        Ctx: WebServiceActionContext;
    begin
        // [GIVEN] A WebServiceActionContext
        // [WHEN]  SetObjectId is called with two different values
        // [THEN]  GetObjectId returns the last-set value
        Assert.AreNotEqual(
            Src.SetObjectIdAndGet(Ctx, 1),
            Src.SetObjectIdAndGet(Ctx, 99),
            'ObjectId 1 and 99 must differ');
    end;

    [Test]
    procedure DefaultObjectId_IsZero()
    var
        Ctx: WebServiceActionContext;
    begin
        // [GIVEN] A fresh WebServiceActionContext
        // [WHEN]  GetObjectId() is called without prior SetObjectId
        // [THEN]  Returns 0 (default)
        Assert.AreEqual(0, Src.GetDefaultObjectId(Ctx), 'Default ObjectId should be 0');
    end;

    // ==================================================================
    // ObjectType — no-throw (ObjectType enum type; cannot compare to Integer in AL)
    // ==================================================================

    [Test]
    procedure SetObjectType_DoesNotThrow()
    var
        Ctx: WebServiceActionContext;
    begin
        // [GIVEN] A WebServiceActionContext
        // [WHEN]  SetObjectType and GetObjectType are called
        // [THEN]  No error is raised
        Src.SetObjectTypeNoThrow(Ctx);
    end;

    // ==================================================================
    // ResultCode — no-throw (= operator not supported for WebServiceActionResultCode in AL BC 26-28)
    // ==================================================================

    [Test]
    procedure SetResultCode_DoesNotThrow()
    var
        Ctx: WebServiceActionContext;
    begin
        // [GIVEN] A WebServiceActionContext
        // [WHEN]  SetResultCode(Created) and GetResultCode() are called
        // [THEN]  No error is raised
        Src.SetResultCodeNoThrow(Ctx);
    end;

    // ==================================================================
    // AddEntityKey — must not throw
    // ==================================================================

    [Test]
    procedure AddEntityKey_DoesNotThrow()
    var
        Ctx: WebServiceActionContext;
    begin
        // [GIVEN] A WebServiceActionContext
        // [WHEN]  AddEntityKey is called with table ID 18 (Customer), field No., value 10000
        // [THEN]  No error is raised
        Assert.IsTrue(Src.CallAddEntityKey(Ctx), 'AddEntityKey should not throw');
    end;
}
