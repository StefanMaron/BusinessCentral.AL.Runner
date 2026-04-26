/// Table used by RecordRef gap tests (FieldExist by name, FullyQualifiedName) and Media tests.
table 310400 "Misc Gaps Table"
{
    DataClassification = CustomerContent;
    fields
    {
        field(1; "No."; Code[20]) { }
        field(2; Name; Text[100]) { }
        field(3; Amount; Decimal) { }
        field(4; MediaField; Media) { }
    }
    keys
    {
        key(PK; "No.") { Clustered = true; }
    }
}

/// Blob table used to create InStream/OutStream values in tests.
table 310401 "Misc Gaps Blob Table"
{
    fields
    {
        field(1; PK; Integer) { }
        field(2; BlobData; Blob) { }
    }
    keys { key(PK; PK) { } }
}

/// Helper codeunit for RecordRef gap tests.
codeunit 310402 "Misc Gaps RecordRef Helper"
{
    /// RecordRef.FieldExist(Text) — look up a field by name.
    procedure FieldExistByName(TableNo: Integer; FieldName: Text): Boolean
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(TableNo);
        exit(RecRef.FieldExist(FieldName));
    end;

    /// RecordRef.FullyQualifiedName() — returns CompanyName$TableName.
    procedure GetFullyQualifiedName(TableNo: Integer): Text
    var
        RecRef: RecordRef;
    begin
        RecRef.Open(TableNo);
        exit(RecRef.FullyQualifiedName());
    end;
}

/// Helper codeunit for Version.Create(4-arg) test.
codeunit 310403 "Misc Gaps Version Helper"
{
    /// Version.Create(major, minor, build, revision) via local Integer variables.
    procedure CreateVersion(Major: Integer; Minor: Integer; Build: Integer; Revision: Integer): Version
    begin
        exit(Version.Create(Major, Minor, Build, Revision));
    end;
}

/// Target codeunit for Codeunit.Run(Text, Table) test.
codeunit 310404 "Misc Gaps Run Target"
{
    TableNo = "Misc Gaps Table";

    trigger OnRun()
    begin
        // Increment Amount on the record passed to OnRun.
        Rec.Amount += 1;
        Rec.Modify();
    end;
}

/// Helper codeunit that calls Codeunit.Run(Text, var Record) using a Text name.
codeunit 310405 "Misc Gaps Codeunit Run Helper"
{
    procedure RunByTextName(var Rec: Record "Misc Gaps Table")
    var
        CUName: Text;
    begin
        CUName := 'Misc Gaps Run Target';
        Codeunit.Run(CUName, Rec);
    end;
}

/// Helper codeunit for Media.ImportStream(InStream, Text, Text, Text) — 4-arg form.
codeunit 310406 "Misc Gaps Media Helper"
{
    procedure ImportStream4Arg(var Rec: Record "Misc Gaps Table"; var InStr: InStream; FileName: Text; MimeType: Text; Description: Text)
    begin
        Rec.MediaField.ImportStream(InStr, FileName, MimeType, Description);
    end;

    procedure HasValue(var Rec: Record "Misc Gaps Table"): Boolean
    begin
        exit(Rec.MediaField.HasValue());
    end;

    procedure MakeBlobInStream(var BlobRec: Record "Misc Gaps Blob Table" temporary; var InStr: InStream)
    var
        OutStr: OutStream;
    begin
        BlobRec.BlobData.CreateOutStream(OutStr);
        OutStr.WriteText('test-content');
        BlobRec.BlobData.CreateInStream(InStr);
    end;
}

/// Helper codeunit for DataTransfer.AddDestinationFilter(Integer, Text, Joker).
codeunit 310407 "Misc Gaps DataTransfer Helper"
{
    procedure AddDestinationFilterVariant(TableNoFrom: Integer; TableNoTo: Integer)
    var
        DT: DataTransfer;
    begin
        DT.SetTables(TableNoFrom, TableNoTo);
        DT.AddDestinationFilter(1, '310400', 310400);
    end;
}

/// Helper codeunit for Database.SelectLatestVersion(Integer).
codeunit 310408 "Misc Gaps Database Helper"
{
    procedure CallSelectLatestVersionWithCompany(CompanyId: Integer)
    begin
        Database.SelectLatestVersion(CompanyId);
    end;
}

/// Helper codeunit for Session.LogMessage 9-arg form.
codeunit 310409 "Misc Gaps Session Helper"
{
    procedure LogMessage9Arg(
        EventId: Text;
        Msg: Text;
        Verb: Verbosity;
        DC: DataClassification;
        Scope: TelemetryScope;
        Dim1Key: Text;
        Dim1Value: Text;
        Dim2Key: Text;
        Dim2Value: Text)
    begin
        Session.LogMessage(EventId, Msg, Verb, DC, Scope, Dim1Key, Dim1Value, Dim2Key, Dim2Value);
    end;
}
