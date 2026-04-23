/// Tests for four missing method overloads:
///   #1180 — Report instance .Execute(XmlText)
///   #1183 — File.Create() returns Boolean
///   #1187 — RecordRef.AddLink(Url, Description)
///   #1192 — RecordRef.GetView(UseNames)
codeunit 302101 "OG Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "OG Src";

    // ──────────────────────────────────────────────────────────────────────────
    // #1180 — Report.Execute(XmlText) — instance method, no-op in standalone
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    procedure Report_Execute_XmlText_IsNoOp()
    var
        Rep: Report "OG Dummy Report";
    begin
        // Must compile and not throw.  Entire claim: this does not crash.
        Src.CallReportExecute(Rep, '<Report />');
    end;

    // ──────────────────────────────────────────────────────────────────────────
    // #1183 — File.Create() returns Boolean (positive: returns true)
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    procedure File_Create_ReturnsTrue()
    var
        F: File;
    begin
        Assert.IsTrue(Src.CreateFileReturnsTrue(F, 'test.txt'),
            'File.Create should return true on success');
    end;

    [Test]
    procedure File_Create_ResultCanBeNegated()
    var
        F: File;
    begin
        // NOT operator on the return value must compile (CS0023 guard).
        Assert.IsFalse(Src.CanNegatCreateResult(F, 'test.txt'),
            'NOT File.Create should return false when Create succeeds');
    end;

    // ──────────────────────────────────────────────────────────────────────────
    // #1187 — RecordRef.AddLink(Url, Description) — returns link ID (0 in stub)
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    procedure RecordRef_AddLink_ReturnsZero()
    var
        Rec: Record "OG Table";
        RecRef: RecordRef;
        LinkId: Integer;
    begin
        RecRef.GetTable(Rec);
        LinkId := Src.AddLinkReturnsId(RecRef, 'https://example.com', 'Example Link');
        Assert.AreEqual(0, LinkId, 'AddLink should return 0 (stub no-op)');
    end;

    [Test]
    procedure RecordRef_AddLink_UrlOnly_ReturnsZero()
    var
        Rec: Record "OG Table";
        RecRef: RecordRef;
    begin
        RecRef.GetTable(Rec);
        // 1-arg form: description defaults to empty
        Assert.AreEqual(0, Src.AddLinkReturnsId(RecRef, 'https://example.com', ''),
            'AddLink with empty description should return 0');
    end;

    // ──────────────────────────────────────────────────────────────────────────
    // #1192 — RecordRef.GetView(UseNames) — 1-argument overload
    // ──────────────────────────────────────────────────────────────────────────

    [Test]
    procedure RecordRef_GetView_UseNames_True_ReturnsText()
    var
        Rec: Record "OG Table";
        RecRef: RecordRef;
        ViewText: Text;
    begin
        RecRef.Open(Database::"OG Table");
        ViewText := Src.GetViewWithUseNames(RecRef, true);
        // Must compile and return the same value as the 0-arg overload.
        Assert.AreEqual(RecRef.GetView(), ViewText,
            'GetView(true) should return the same view as GetView()');
    end;

    [Test]
    procedure RecordRef_GetView_UseNames_False_ReturnsText()
    var
        Rec: Record "OG Table";
        RecRef: RecordRef;
        ViewText: Text;
    begin
        RecRef.Open(Database::"OG Table");
        ViewText := Src.GetViewWithUseNames(RecRef, false);
        Assert.AreEqual(RecRef.GetView(), ViewText,
            'GetView(false) should return the same view as GetView()');
    end;
}
