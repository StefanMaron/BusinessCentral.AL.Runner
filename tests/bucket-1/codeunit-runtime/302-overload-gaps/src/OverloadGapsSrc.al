/// Source codeunit exercising the 4 missing method overloads.
/// Issues: #1180 (Report.Execute), #1183 (File.Create bool return),
///         #1187 (RecordRef.AddLink), #1192 (RecordRef.GetView(false)).
codeunit 302100 "OG Src"
{
    // Issue #1180 — Report.Execute(XmlText) on an instance variable.
    procedure CallReportExecute(var Rep: Report "OG Dummy Report"; XmlText: Text)
    begin
        Rep.Execute(XmlText);
    end;

    // Issue #1183 — File.Create() returns Boolean.
    procedure CreateFileReturnsTrue(var F: File; FileName: Text): Boolean
    begin
        exit(F.Create(FileName));
    end;

    // Negative: result of File.Create can be tested with NOT operator (the bug: CS0023 void).
    procedure CanNegatCreateResult(var F: File; FileName: Text): Boolean
    begin
        exit(not F.Create(FileName));
    end;

    // Issue #1187 — RecordRef.AddLink(Url, Description) returns Integer (link ID).
    procedure AddLinkReturnsId(var RecRef: RecordRef; Url: Text; Description: Text): Integer
    begin
        exit(RecRef.AddLink(Url, Description));
    end;

    // Issue #1192 — RecordRef.GetView(UseNames) — 1-argument overload.
    procedure GetViewWithUseNames(var RecRef: RecordRef; UseNames: Boolean): Text
    begin
        exit(RecRef.GetView(UseNames));
    end;
}
