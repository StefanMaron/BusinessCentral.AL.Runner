/// Source helpers for testing methods that must return Boolean — issue #1432.
/// AL uses these methods in `if not Method(...)` guards, so the runner mocks
/// must return bool, not void.
codeunit 1314001 "VRB Src"
{
    /// IsolatedStorage.Set(key, value) — must return Boolean (true on success).
    procedure IsoSet_ReturnsTrue(StorageKey: Text; StorageValue: Text): Boolean
    var
        Result: Boolean;
    begin
        Result := IsolatedStorage.Set(StorageKey, StorageValue);
        exit(Result);
    end;

    /// IsolatedStorage.Set(key, value, DataScope) — must return Boolean.
    procedure IsoSetWithScope_ReturnsTrue(StorageKey: Text; StorageValue: Text; Scope: DataScope): Boolean
    var
        Result: Boolean;
    begin
        Result := IsolatedStorage.Set(StorageKey, StorageValue, Scope);
        exit(Result);
    end;

    /// Exercise the `if not IsolatedStorage.Set(...)` guard pattern from issue #1432.
    /// Returns true if the branch was NOT taken (i.e. Set returned true).
    procedure IsoSet_IfNotGuard(StorageKey: Text; StorageValue: Text): Boolean
    begin
        if not IsolatedStorage.Set(StorageKey, StorageValue) then
            exit(false);
        exit(true);
    end;

    /// XmlElement.AddBeforeSelf — must return Boolean.
    /// Returns true if the insert succeeded (i.e. AddBeforeSelf returned true).
    procedure XmlAddBeforeSelf_IfNotGuard(ChildName: Text; SiblingName: Text): Boolean
    var
        ParentDoc: XmlDocument;
        Root: XmlElement;
        Child: XmlElement;
        Sibling: XmlElement;
    begin
        Root := XmlElement.Create('root');
        ParentDoc := XmlDocument.Create();
        ParentDoc.Add(Root);
        Child := XmlElement.Create(ChildName);
        Root.Add(Child);
        Sibling := XmlElement.Create(SiblingName);
        if not Child.AddBeforeSelf(Sibling.AsXmlNode()) then
            exit(false);
        exit(true);
    end;

    /// XmlElement.AddAfterSelf — must return Boolean.
    procedure XmlAddAfterSelf_IfNotGuard(ChildName: Text; SiblingName: Text): Boolean
    var
        ParentDoc: XmlDocument;
        Root: XmlElement;
        Child: XmlElement;
        Sibling: XmlElement;
    begin
        Root := XmlElement.Create('root');
        ParentDoc := XmlDocument.Create();
        ParentDoc.Add(Root);
        Child := XmlElement.Create(ChildName);
        Root.Add(Child);
        Sibling := XmlElement.Create(SiblingName);
        if not Child.AddAfterSelf(Sibling.AsXmlNode()) then
            exit(false);
        exit(true);
    end;

    /// XmlElement.Remove — must return Boolean.
    procedure XmlRemove_IfNotGuard(ChildName: Text): Boolean
    var
        ParentDoc: XmlDocument;
        Root: XmlElement;
        Child: XmlElement;
    begin
        Root := XmlElement.Create('root');
        ParentDoc := XmlDocument.Create();
        ParentDoc.Add(Root);
        Child := XmlElement.Create(ChildName);
        Root.Add(Child);
        if not Child.Remove() then
            exit(false);
        exit(true);
    end;

    /// ReportInstance.SaveAsPdf — must return Boolean.
    procedure ReportSaveAsPdf_IfNotGuard(FileName: Text): Boolean
    var
        Rep: Report "VRB Empty Report";
    begin
        if not Rep.SaveAsPdf(FileName) then
            exit(false);
        exit(true);
    end;

    /// ReportInstance.SaveAs (with Format + OutStream) — must return Boolean.
    procedure ReportSaveAs_IfNotGuard(ReqParams: Text; Fmt: ReportFormat; var OutStr: OutStream): Boolean
    var
        Rep: Report "VRB Empty Report";
    begin
        if not Rep.SaveAs(ReqParams, Fmt, OutStr) then
            exit(false);
        exit(true);
    end;
}
