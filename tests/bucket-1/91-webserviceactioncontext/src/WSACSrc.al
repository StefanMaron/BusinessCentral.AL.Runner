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
    begin
        Ctx.SetObjectType(ObjectType::Codeunit);
        Ctx.GetObjectType();
    end;

    // ResultCode — no-throw only; = operator not supported for WebServiceActionResultCode in AL 26-28
    procedure SetResultCodeNoThrow(var Ctx: WebServiceActionContext)
    begin
        Ctx.SetResultCode(WebServiceActionResultCode::Created);
        Ctx.GetResultCode();
    end;

    // AddEntityKey — no-throw; uses integer table ID to avoid base-app dependency
    procedure CallAddEntityKey(var Ctx: WebServiceActionContext): Boolean
    begin
        Ctx.AddEntityKey(18, 'No.', '10000');
        exit(true);
    end;
}
