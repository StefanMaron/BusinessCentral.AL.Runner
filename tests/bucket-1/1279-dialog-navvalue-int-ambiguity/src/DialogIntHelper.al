table 1279001 "DNIA Job"
{
    DataClassification = ToBeClassified;

    fields
    {
        field(1; "No."; Code[20]) { DataClassification = ToBeClassified; }
        field(2; Description; Text[100]) { DataClassification = ToBeClassified; }
        field(3; "Line Count"; Integer) { DataClassification = ToBeClassified; }
    }

    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

codeunit 1279001 "DNIA Helper"
{
    /// <summary>
    /// Mimics the telemetry scenario: Dialog.Update(3, "No.") where "No." is a Code field.
    /// BC compiler emits ALUpdate(int, NavValue) but the int overload ALUpdate(int, int)
    /// also matches because NavValue has implicit conversion to int, causing CS0121.
    /// </summary>
    procedure ShowJobProgress(JobNo: Code[20]): Text
    var
        Window: Dialog;
    begin
        Window.Open('Processing Job #1######## Lines #2######');
        Window.Update(1, JobNo);
        // This is the problematic call: field number 3, Code field value
        // NavValue wrapping a Code should not be ambiguous with the int overload
        Window.Update(3, JobNo);
        Window.Close();
        exit('Processed:' + JobNo);
    end;

    /// <summary>
    /// Dialog.Update with an Integer field value — must still work.
    /// </summary>
    procedure ShowCountInDialog(Count: Integer): Text
    var
        Window: Dialog;
    begin
        Window.Open('Count #1######');
        Window.Update(1, Count);
        Window.Close();
        exit('Count:' + Format(Count));
    end;
}
