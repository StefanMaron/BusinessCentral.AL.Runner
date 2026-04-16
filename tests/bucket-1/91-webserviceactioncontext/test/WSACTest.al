codeunit 95101 "WSAC Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "WSAC Src";

    // ==================================================================
    // ObjectId — round-trip get/set
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
        // [WHEN]  SetObjectId is called with different values
        // [THEN]  GetObjectId distinguishes them
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
        // [WHEN]  GetObjectId() called with no prior SetObjectId
        // [THEN]  Returns 0
        Assert.AreEqual(0, Src.GetDefaultObjectId(Ctx), 'Default ObjectId should be 0');
    end;

    // ==================================================================
    // ObjectType — round-trip get/set
    // ==================================================================

    [Test]
    procedure SetObjectType_GetObjectType_RoundTrip()
    var
        Ctx: WebServiceActionContext;
    begin
        // [GIVEN] A WebServiceActionContext
        // [WHEN]  SetObjectType(22) is called
        // [THEN]  GetObjectType() returns 22
        Assert.AreEqual(22, Src.SetObjectTypeAndGet(Ctx, 22), 'ObjectType should round-trip to 22');
    end;

    [Test]
    procedure DefaultObjectType_IsZero()
    var
        Ctx: WebServiceActionContext;
    begin
        // [GIVEN] A fresh WebServiceActionContext
        // [WHEN]  GetObjectType() called with no prior SetObjectType
        // [THEN]  Returns 0
        Assert.AreEqual(0, Src.GetDefaultObjectType(Ctx), 'Default ObjectType should be 0');
    end;

    // ==================================================================
    // ResultCode — enum round-trip
    // ==================================================================

    [Test]
    procedure SetResultCode_Created_RoundTrips()
    var
        Ctx: WebServiceActionContext;
    begin
        // [GIVEN] A WebServiceActionContext
        // [WHEN]  SetResultCode(Created) then GetResultCode()
        // [THEN]  GetResultCode() = Created
        Assert.IsTrue(Src.SetCreatedCodeAndVerify(Ctx), 'ResultCode should round-trip to Created');
    end;

    [Test]
    procedure SetResultCode_OkResponse_RoundTrips()
    var
        Ctx: WebServiceActionContext;
    begin
        // [GIVEN] A WebServiceActionContext
        // [WHEN]  SetResultCode(OkResponse) then GetResultCode()
        // [THEN]  GetResultCode() = OkResponse
        Assert.IsTrue(Src.SetOkResponseAndVerify(Ctx), 'ResultCode should round-trip to OkResponse');
    end;

    [Test]
    procedure CreatedAndOkResponse_AreDifferent()
    begin
        // [GIVEN] Two WebServiceActionContext variables
        // [WHEN]  Each is set to a different ResultCode
        // [THEN]  The codes are distinguishable
        Assert.IsTrue(Src.CreatedAndOkResponseDiffer(), 'Created <> OkResponse');
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
        // [WHEN]  AddEntityKey is called
        // [THEN]  No error
        Assert.IsTrue(Src.CallAddEntityKey(Ctx), 'AddEntityKey should not throw');
    end;
}
