/// Source table and page for the TestField.Lookup(RecordRef) test.
table 310200 "MOv Rec"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Code; Code[20]) { }
        field(3; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}

page 310200 "MOv Card"
{
    PageType = Card;
    SourceTable = "MOv Rec";
    layout
    {
        area(Content)
        {
            field(CodeField; Rec.Code) { }
            field(NameField; Rec.Name) { }
        }
    }
}

/// Source helpers for the missing-overloads gap tests (issue #1380).
/// Tests Dialog.Error(Text,Joker), ErrorInfo.AddAction/AddNavigationAction 4-arg,
/// FilterPageBuilder.AddField(Text,Joker,Text), and TestField.Lookup(RecordRef).
codeunit 310201 "MOv Src"
{
    // ── Dialog.Error(Text, Joker) ────────────────────────────────────────────

    procedure ErrorWithIntArg(intVal: Integer)
    begin
        Error('Value is %1', intVal);
    end;

    procedure ErrorWithTextArg(textVal: Text)
    begin
        Error('Name is %1', textVal);
    end;

    // ── ErrorInfo.AddAction (Text, Integer, Text, Text) ─────────────────────
    // 4th arg is the serialized action *parameters* string.

    procedure EI_AddAction_4Arg(caption: Text; paramStr: Text): Boolean
    var
        ei: ErrorInfo;
    begin
        ei.AddAction(caption, Codeunit::"MOv Src", 'DoNothing', paramStr);
        exit(true);
    end;

    // ── ErrorInfo.AddNavigationAction (Text, Text) ──────────────────────────

    procedure EI_AddNavigationAction_2Arg(caption: Text; description: Text): Boolean
    var
        ei: ErrorInfo;
    begin
        ei.AddNavigationAction(caption, description);
        exit(true);
    end;

    procedure EI_AddNavigationAction_MessagePreserved(): Text
    var
        ei: ErrorInfo;
    begin
        ei.Message := 'Test error';
        ei.AddNavigationAction('Open page', 'Navigate to the related record');
        exit(ei.Message);
    end;

    // ── FilterPageBuilder.AddField (Text, Joker, Text) ─────────────────────

    procedure FPB_AddField_3Arg(caption: Text; defaultFilter: Text): Boolean
    var
        fpb: FilterPageBuilder;
        rec: Record "MOv Rec";
    begin
        fpb.AddField(caption, rec.Code, defaultFilter);
        exit(true);
    end;

    procedure FPB_AddField_3Arg_Count(): Integer
    var
        fpb: FilterPageBuilder;
        rec: Record "MOv Rec";
    begin
        fpb.AddField('Code', rec.Code, 'A*');
        fpb.AddField('Name', rec.Name, 'Test*');
        exit(fpb.Count);
    end;

    /// Target method referenced by AddAction — must exist for AL compilability.
    procedure DoNothing(ei: ErrorInfo)
    begin
    end;
}
