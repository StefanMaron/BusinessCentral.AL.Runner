/// Helper codeunit proving the "always false" contract for Variant.IsX() stubs
/// where the named type is either not assignable to a Variant in BC AL (e.g.
/// Action, Binary, File, DotNet, FilterPageBuilder, XmlAttributeCollection) or
/// is an enum that collapses to NavOption in the standalone runner and therefore
/// cannot be distinguished from a generic Option value (e.g. DataClassification,
/// ExecutionMode, SecurityFiltering, TableConnectionType, etc.).
/// Closes #1401.
codeunit 319001 "VSI Src"
{
    // For each stub we only exercise the false case (v := Integer; v.IsX()),
    // since the named types are either non-Variant-assignable in BC AL or
    // collapse to NavOption — there is no AL syntax to get a true result.

    // ── Action (false-only) ────────────────────────────────────────────────────
    // Action is a page action object — not directly Variant-assignable in BC AL.

    procedure IsAction_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsAction());
    end;

    // ── Automation (false-only) ────────────────────────────────────────────────
    // Automation (OLE Automation) requires COM interop — not available standalone.

    procedure IsAutomation_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsAutomation());
    end;

    // ── Binary (false-only) ────────────────────────────────────────────────────
    // Binary is a blob-like type with no standalone mock; not Variant-assignable.

    procedure IsBinary_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsBinary());
    end;

    // ── ClientType (false-only) ────────────────────────────────────────────────
    // ClientType is an enum that collapses to NavOption in the runner; the runner
    // cannot distinguish it from a generic Option value.

    procedure IsClientType_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsClientType());
    end;

    // ── DataClassification (false-only) ────────────────────────────────────────
    // DataClassification is an enum → NavOption; indistinguishable from Option.

    procedure IsDataClassification_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsDataClassification());
    end;

    // ── DataClassificationType (false-only) ────────────────────────────────────
    // DataClassificationType is an enum → NavOption; indistinguishable from Option.

    procedure IsDataClassificationType_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsDataClassificationType());
    end;

    // ── DefaultLayout (false-only) ─────────────────────────────────────────────
    // DefaultLayout is an enum → NavOption; indistinguishable from Option.

    procedure IsDefaultLayout_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsDefaultLayout());
    end;

    // ── DotNet (false-only) ────────────────────────────────────────────────────
    // DotNet interop is not available in standalone mode.

    procedure IsDotNet_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsDotNet());
    end;

    // ── ExecutionMode (false-only) ─────────────────────────────────────────────
    // ExecutionMode is an enum → NavOption; indistinguishable from Option.

    procedure IsExecutionMode_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsExecutionMode());
    end;

    // ── File (false-only) ──────────────────────────────────────────────────────
    // File has no standalone mock; not Variant-assignable.

    procedure IsFile_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsFile());
    end;

    // ── ObjectType (false-only) ────────────────────────────────────────────────
    // ObjectType is an enum → NavOption; indistinguishable from Option.

    procedure IsObjectType_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsObjectType());
    end;

    // ── SecurityFiltering (false-only) ─────────────────────────────────────────
    // SecurityFiltering is an enum → NavOption; indistinguishable from Option.

    procedure IsSecurityFiltering_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsSecurityFiltering());
    end;

    // ── TableConnectionType (false-only) ──────────────────────────────────────
    // TableConnectionType is an enum → NavOption; indistinguishable from Option.

    procedure IsTableConnectionType_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsTableConnectionType());
    end;

    // ── TestPermissions (false-only) ───────────────────────────────────────────
    // TestPermissions is an enum → NavOption; indistinguishable from Option.

    procedure IsTestPermissions_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsTestPermissions());
    end;

    // ── TextConstant (false-only) ──────────────────────────────────────────────
    // TextConstant is not directly Variant-assignable in BC AL.

    procedure IsTextConstant_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsTextConstant());
    end;

    // ── TextEncoding (false-only) ──────────────────────────────────────────────
    // TextEncoding is an enum → NavOption; indistinguishable from Option.

    procedure IsTextEncoding_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsTextEncoding());
    end;

    // ── TransactionType (false-only) ───────────────────────────────────────────
    // TransactionType is an enum → NavOption; indistinguishable from Option.

    procedure IsTransactionType_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsTransactionType());
    end;

    // ── WideChar (false-only) ──────────────────────────────────────────────────
    // WideChar is not directly Variant-assignable in BC AL (use Char instead).

    procedure IsWideChar_ForInteger(): Boolean
    var
        v: Variant;
    begin
        v := 42;
        exit(v.IsWideChar());
    end;
}
