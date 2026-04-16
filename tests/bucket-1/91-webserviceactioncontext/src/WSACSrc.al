codeunit 95100 "WSAC Src"
{
    procedure SetObjectIdAndGet(var Ctx: WebServiceActionContext; ObjId: Integer): Integer
    begin
        Ctx.SetObjectId(ObjId);
        exit(Ctx.GetObjectId());
    end;

    procedure SetObjectTypeAndGet(var Ctx: WebServiceActionContext; ObjType: Integer): Integer
    begin
        Ctx.SetObjectType(ObjType);
        exit(Ctx.GetObjectType());
    end;

    procedure SetCreatedCodeAndVerify(var Ctx: WebServiceActionContext): Boolean
    begin
        Ctx.SetResultCode(WebServiceActionResultCode::Created);
        exit(Ctx.GetResultCode() = WebServiceActionResultCode::Created);
    end;

    procedure SetOkResponseAndVerify(var Ctx: WebServiceActionContext): Boolean
    begin
        Ctx.SetResultCode(WebServiceActionResultCode::OkResponse);
        exit(Ctx.GetResultCode() = WebServiceActionResultCode::OkResponse);
    end;

    procedure CreatedAndOkResponseDiffer(): Boolean
    var
        Ctx1: WebServiceActionContext;
        Ctx2: WebServiceActionContext;
    begin
        Ctx1.SetResultCode(WebServiceActionResultCode::Created);
        Ctx2.SetResultCode(WebServiceActionResultCode::OkResponse);
        exit(Ctx1.GetResultCode() <> Ctx2.GetResultCode());
    end;

    procedure CallAddEntityKey(var Ctx: WebServiceActionContext): Boolean
    begin
        Ctx.AddEntityKey(Database::Customer, 'No.', '10000');
        exit(true);
    end;

    procedure GetDefaultObjectId(var Ctx: WebServiceActionContext): Integer
    begin
        exit(Ctx.GetObjectId());
    end;

    procedure GetDefaultObjectType(var Ctx: WebServiceActionContext): Integer
    begin
        exit(Ctx.GetObjectType());
    end;
}
