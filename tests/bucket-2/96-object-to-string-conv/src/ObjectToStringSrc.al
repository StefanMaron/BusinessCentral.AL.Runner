table 305010 "Obj To Str Table"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Code"; Code[20])
        {
            Caption = 'Code';
        }
        field(2; "Description"; Text[100])
        {
            Caption = 'Description';
        }
    }

    keys
    {
        key(PK; "Code")
        {
            Clustered = true;
        }
    }
}

codeunit 305010 "Obj To Str Helper"
{
    procedure ErrorFromVariant(Msg: Variant)
    begin
        Error(Msg);
    end;

    procedure ErrorFromVariantFmt(Fmt: Variant; Val: Variant)
    begin
        Error(Fmt, Val);
    end;

    procedure StrSubstNoFromVariant(Fmt: Variant; Val: Variant): Text
    begin
        exit(StrSubstNo(Fmt, Val));
    end;

    procedure CopyStrFromVariant(S: Variant): Text
    begin
        exit(CopyStr(S, 2, 3));
    end;

    procedure LowerFromVariant(S: Variant): Text
    begin
        exit(LowerCase(S));
    end;

    procedure UpperFromVariant(S: Variant): Text
    begin
        exit(UpperCase(S));
    end;

    procedure PadStrFromVariant(S: Variant): Text
    begin
        exit(PadStr(S, 8));
    end;

    procedure IncStrFromVariant(S: Variant): Text
    begin
        exit(IncStr(S));
    end;

    procedure DelChrFromVariant(S: Variant): Text
    begin
        exit(DelChr(S, '=', ' '));
    end;

    procedure SetFilterFromVariantExpr(var Rec: Record "Obj To Str Table"; FilterExpr: Variant)
    begin
        Rec.SetFilter("Code", FilterExpr);
    end;

    procedure SetFilterFromVariantExprAndArg(var Rec: Record "Obj To Str Table"; FilterExpr: Variant; Val: Variant)
    begin
        Rec.SetFilter("Code", FilterExpr, Val);
    end;

    procedure MessageFromVariant(Msg: Variant)
    begin
        Message(Msg);
    end;
}
