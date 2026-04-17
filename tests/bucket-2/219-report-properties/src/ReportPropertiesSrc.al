table 60480 "RPR Row"
{
    fields
    {
        field(1; "Id"; Integer) { }
    }
    keys { key(PK; "Id") { Clustered = true; } }
}

report 60480 "RPR Simple"
{
    UsageCategory = Tasks;
    ApplicationArea = All;
    dataset
    {
        dataitem(Main; "RPR Row")
        {
            column(Id; "Id") { }
        }
    }
}

/// Exercises Report properties: Preview, PreviewCanPrint, UseRequestPage,
/// Language, FormatRegion.
codeunit 60480 "RPR Src"
{
    procedure UseRequestPage_SetAndGet(): Boolean
    var
        rep: Report "RPR Simple";
    begin
        rep.UseRequestPage := false;
        exit(rep.UseRequestPage);
    end;

    procedure Language_SetAndGet(): Integer
    var
        rep: Report "RPR Simple";
    begin
        rep.Language := 1033;
        exit(rep.Language);
    end;

    procedure FormatRegion_SetAndGet(): Text
    var
        rep: Report "RPR Simple";
    begin
        rep.FormatRegion := 'en-US';
        exit(rep.FormatRegion);
    end;
}
