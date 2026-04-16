/// Source codeunit exercising static Page.* method calls.
/// Each procedure calls one static Page method so the test can verify no error.
codeunit 89100 "PST Source"
{
    procedure CallPageRun(PageId: Integer)
    begin
        Page.Run(PageId);
    end;

    procedure CallPageRunModal(PageId: Integer): Integer
    begin
        exit(Page.RunModal(PageId));
    end;

    procedure CallPageActivate()
    begin
        Page.Activate();
    end;

    procedure CallPageSaveRecord()
    begin
        Page.SaveRecord();
    end;

    procedure CallPageUpdate()
    begin
        Page.Update();
    end;

    procedure CallPageUpdateBool(DoUpdate: Boolean)
    begin
        Page.Update(DoUpdate);
    end;

    procedure CallPageSetTableView(var Rec: Record "PST Record")
    begin
        Page.SetTableView(Rec);
    end;

    procedure CallPageSetSelectionFilter(var Rec: Record "PST Record")
    begin
        Page.SetSelectionFilter(Rec);
    end;

    procedure CallPageSetRecord(var Rec: Record "PST Record")
    begin
        Page.SetRecord(Rec);
    end;

    procedure CallPageObjectId()
    begin
        Page.ObjectId(false);
    end;

    procedure GetPageLookupMode(): Boolean
    begin
        exit(Page.LookupMode);
    end;

    procedure SetPageLookupMode(Value: Boolean)
    begin
        Page.LookupMode := Value;
    end;

    procedure CallCancelBackgroundTask(TaskId: Integer)
    begin
        Page.CancelBackgroundTask(TaskId);
    end;

    procedure CallSetBackgroundTaskResult(Result: Dictionary of [Text, Text])
    begin
        Page.SetBackgroundTaskResult(Result);
    end;

    procedure CallGetBackgroundParameters(var Params: Dictionary of [Text, Text])
    begin
        Page.GetBackgroundParameters(Params);
    end;

    procedure CallEnqueueBackgroundTask(var TaskId: Integer; PageId: Integer)
    var
        Params: Dictionary of [Text, Text];
    begin
        Page.EnqueueBackgroundTask(TaskId, PageId, Params);
    end;
}

table 89100 "PST Record"
{
    fields
    {
        field(1; Id; Integer) { }
        field(2; Name; Text[100]) { }
    }
    keys { key(PK; Id) { Clustered = true; } }
}
