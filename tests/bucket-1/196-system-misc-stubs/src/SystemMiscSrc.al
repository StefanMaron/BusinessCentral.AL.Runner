/// Source codeunit exercising System misc stubs:
/// Format (3-arg with mask), GetUrl, GetDocumentUrl, CaptionClassTranslate,
/// CanLoadType, CodeCoverage*, ImportObjects, ExportObjects,
/// ImportStreamWithUrlAccess, GetDotNetType.
codeunit 100300 "SystemMisc Src"
{
    // ── Format 3-arg ─────────────────────────────────────────────────────────

    /// Format a decimal with a custom AL mask; must return non-empty text.
    procedure FormatWithMask(V: Decimal; Mask: Text): Text
    begin
        exit(Format(V, 0, Mask));
    end;

    // ── GetUrl ────────────────────────────────────────────────────────────────

    /// GetUrl with 4 arguments (ClientType, Company, ObjectType, ObjectId).
    procedure GetPageUrl(ObjectId: Integer): Text
    begin
        exit(GetUrl(ClientType::Current, CompanyName(), ObjectType::Page, ObjectId));
    end;

    // ── GetDocumentUrl ────────────────────────────────────────────────────────

    /// GetDocumentUrl must not throw; returns empty string stub.
    procedure GetDocumentUrlStub(): Text
    var
        MediaId: Guid;
    begin
        MediaId := CreateGuid();
        exit(GetDocumentUrl(MediaId));
    end;

    // ── CaptionClassTranslate ─────────────────────────────────────────────────

    /// CaptionClassTranslate must not throw; returns non-error text.
    procedure TranslateCaption(CaptionExpr: Text): Text
    begin
        exit(CaptionClassTranslate(CaptionExpr));
    end;

    // ── CodeCoverage* ─────────────────────────────────────────────────────────

    /// Start code coverage; must not throw.
    procedure StartCoverage()
    begin
        CodeCoverageLoad();
        CodeCoverageLog(true);
    end;

    /// Stop code coverage; must not throw.
    procedure StopCoverage()
    begin
        CodeCoverageLog(false);
        CodeCoverageRefresh();
    end;

    // ── ImportStreamWithUrlAccess ─────────────────────────────────────────────

    /// ImportStreamWithUrlAccess must not throw; returns empty-or-stub URL.
    procedure ImportStreamUrl(): Text
    var
        Rec: Record "SMisc Blob";
        IStream: InStream;
    begin
        Rec."Entry No." := 1;
        Rec.Content.CreateInStream(IStream);
        exit(ImportStreamWithUrlAccess(IStream, 'test.pdf', 60));
    end;
}
