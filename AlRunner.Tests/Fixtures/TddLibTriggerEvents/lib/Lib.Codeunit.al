/// <summary>#5286: where the library's members are generated, into the library's own compile.</summary>
codeunit 72414 "TLib Lib Target"
{
    procedure Placeholder()
    begin
    end;
}

/// <summary>
/// #5286: a subscriber of a database event of the app's table, in a bundle compiled after the one that
/// starts the event. It calls a member the app does not declare.
/// </summary>
codeunit 72410 "TLib Subscriber"
{
    [EventSubscriber(ObjectType::Table, Database::"TLib Rec", 'OnAfterInsertEvent', '', false, false)]
    local procedure HandleAfterInsert(var Rec: Record "TLib Rec"; RunTrigger: Boolean)
    var
        Target: Codeunit "TLib Lib Target";
        Result: Integer;
    begin
        Result := Target.MissingLibEvent(1);
    end;
}

/// <summary>#5286: an OnModify trigger the library adds to the app's table.</summary>
tableextension 72411 "TLib Rec Ext" extends "TLib Rec"
{
    trigger OnModify()
    var
        Target: Codeunit "TLib Lib Target";
        Result: Integer;
    begin
        Result := Target.MissingExtTrigger(1);
    end;
}

/// <summary>#5286: a codeunit run by name from the test bundle and by id from the app.</summary>
codeunit 72412 "TLib Runner"
{
    trigger OnRun()
    var
        Target: Codeunit "TLib Lib Target";
        Result: Integer;
    begin
        Result := Target.MissingLibRun(1);
    end;
}

/// <summary>#5286: a table of the library with an OnInsert trigger; the app starts it through a RecordRef,
/// by table id, without naming the table.</summary>
table 72413 "TLib Lib Rec"
{
    fields { field(1; PK; Code[20]) { } }
    keys { key(PK; PK) { Clustered = true; } }

    trigger OnInsert()
    var
        Target: Codeunit "TLib Lib Target";
        Result: Integer;
    begin
        Result := Target.MissingLibTrigger(1);
    end;
}

/// <summary>#5286: OnAfterInsert on the app's table, and OnAfterValidate of its field in a modify() block.</summary>
tableextension 72415 "TLib Ext Rec Ext" extends "TLib Ext Rec"
{
    fields
    {
        modify(Qty)
        {
            trigger OnAfterValidate()
            var
                Target: Codeunit "TLib Lib Target";
                Result: Integer;
            begin
                Result := Target.MissingExtAfterValidate(1);
            end;
        }
    }

    trigger OnAfterInsert()
    var
        Target: Codeunit "TLib Lib Target";
        Result: Integer;
    begin
        Result := Target.MissingExtAfterInsert(1);
    end;
}
