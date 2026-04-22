/// Helper table providing a Blob field for InStream creation in tests.
table 98100 "NGRE Blob"
{
    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Content; Blob) { }
    }
    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

/// Exercises NavApp.GetResource(ResourceName; var InStream; TextEncoding) — the
/// 3-AL-arg / 4-C#-arg overload (ResourceName + InStream + TextEncoding).
codeunit 98101 "NavApp GetResource Encoding Src"
{
    procedure GetResourceWithEncoding(ResourceName: Text)
    var
        Rec: Record "NGRE Blob";
        IS: InStream;
    begin
        Rec.Content.CreateInStream(IS);
        NavApp.GetResource(ResourceName, IS, TextEncoding::UTF8);
    end;

    procedure GetResourceNoEncoding(ResourceName: Text)
    var
        Rec: Record "NGRE Blob";
        IS: InStream;
    begin
        Rec.Content.CreateInStream(IS);
        NavApp.GetResource(ResourceName, IS);
    end;
}
