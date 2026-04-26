codeunit 95100 "WSAC Src"
{
    // ObjectId — round-trip: set value, get it back
    procedure SetObjectIdAndGet(var Ctx: WebServiceActionContext; ObjId: Integer): Integer
    begin
        Ctx.SetObjectId(ObjId);
        exit(Ctx.GetObjectId());
    end;

    procedure GetDefaultObjectId(var Ctx: WebServiceActionContext): Integer
    begin
        exit(Ctx.GetObjectId());
    end;

    // ObjectType — no-throw only; ObjectType enum cannot be compared to Integer in AL
    procedure SetObjectTypeNoThrow(var Ctx: WebServiceActionContext)
    var
        ObjType: ObjectType;
    begin
        Ctx.SetObjectType(ObjectType::Codeunit);
        ObjType := Ctx.GetObjectType();
    end;

    // ResultCode — no-throw only; = operator not supported for WebServiceActionResultCode in AL 26-28
    procedure SetResultCodeNoThrow(var Ctx: WebServiceActionContext)
    var
        RC: WebServiceActionResultCode;
    begin
        Ctx.SetResultCode(WebServiceActionResultCode::Created);
        RC := Ctx.GetResultCode();
    end;

    // AddEntityKey — no-throw; signature is (Integer fieldId, Variant value)
    procedure CallAddEntityKey(var Ctx: WebServiceActionContext): Boolean
    begin
        Ctx.AddEntityKey(1, '10000');
        exit(true);
    end;
}
