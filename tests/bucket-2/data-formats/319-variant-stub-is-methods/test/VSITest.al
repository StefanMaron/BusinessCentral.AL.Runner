/// Tests proving the "always false" contract for Variant.IsX() stubs.
/// These types are either not Variant-assignable in BC AL (Action, Binary, File,
/// DotNet, TextConstant, WideChar) or are enums that collapse to NavOption in
/// the standalone runner and therefore cannot be distinguished from a generic
/// Option value (ClientType, DataClassification, DataClassificationType,
/// DefaultLayout, ExecutionMode, ObjectType, SecurityFiltering,
/// TableConnectionType, TestPermissions, TextEncoding, TransactionType).
/// In all cases the observable contract is: IsX() returns false.
/// The false case is the only provable case — there is no AL syntax to place
/// these types inside a Variant on this runner. Closes #1401.
codeunit 319002 "VSI Test"
{
    Subtype = Test;

    var
        Assert: Codeunit Assert;
        Src: Codeunit "VSI Src";

    // ── Action ────────────────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsAction_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsAction_ForInteger(),
            'Variant.IsAction must return false (no Action mock in standalone mode)');
    end;

    // ── Automation ────────────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsAutomation_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsAutomation_ForInteger(),
            'Variant.IsAutomation must return false (OLE Automation not available standalone)');
    end;

    // ── Binary ────────────────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsBinary_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsBinary_ForInteger(),
            'Variant.IsBinary must return false (no Binary mock in standalone mode)');
    end;

    // ── ClientType ────────────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsClientType_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsClientType_ForInteger(),
            'Variant.IsClientType must return false (enum collapses to NavOption; indistinguishable)');
    end;

    // ── DataClassification ────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsDataClassification_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsDataClassification_ForInteger(),
            'Variant.IsDataClassification must return false (enum collapses to NavOption; indistinguishable)');
    end;

    // ── DataClassificationType ────────────────────────────────────────────────

    [Test]
    procedure VSI_IsDataClassificationType_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsDataClassificationType_ForInteger(),
            'Variant.IsDataClassificationType must return false (enum collapses to NavOption; indistinguishable)');
    end;

    // ── DefaultLayout ─────────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsDefaultLayout_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsDefaultLayout_ForInteger(),
            'Variant.IsDefaultLayout must return false (enum collapses to NavOption; indistinguishable)');
    end;

    // ── DotNet ────────────────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsDotNet_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsDotNet_ForInteger(),
            'Variant.IsDotNet must return false (DotNet interop not available standalone)');
    end;

    // ── ExecutionMode ─────────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsExecutionMode_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsExecutionMode_ForInteger(),
            'Variant.IsExecutionMode must return false (enum collapses to NavOption; indistinguishable)');
    end;

    // ── File ──────────────────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsFile_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsFile_ForInteger(),
            'Variant.IsFile must return false (no File mock in standalone mode)');
    end;

    // ── ObjectType ────────────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsObjectType_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsObjectType_ForInteger(),
            'Variant.IsObjectType must return false (enum collapses to NavOption; indistinguishable)');
    end;

    // ── SecurityFiltering ─────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsSecurityFiltering_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsSecurityFiltering_ForInteger(),
            'Variant.IsSecurityFiltering must return false (enum collapses to NavOption; indistinguishable)');
    end;

    // ── TableConnectionType ───────────────────────────────────────────────────

    [Test]
    procedure VSI_IsTableConnectionType_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsTableConnectionType_ForInteger(),
            'Variant.IsTableConnectionType must return false (enum collapses to NavOption; indistinguishable)');
    end;

    // ── TestPermissions ───────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsTestPermissions_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsTestPermissions_ForInteger(),
            'Variant.IsTestPermissions must return false (enum collapses to NavOption; indistinguishable)');
    end;

    // ── TextConstant ──────────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsTextConstant_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsTextConstant_ForInteger(),
            'Variant.IsTextConstant must return false (TextConstant not Variant-assignable in BC AL)');
    end;

    // ── TextEncoding ──────────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsTextEncoding_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsTextEncoding_ForInteger(),
            'Variant.IsTextEncoding must return false (enum collapses to NavOption; indistinguishable)');
    end;

    // ── TransactionType ───────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsTransactionType_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsTransactionType_ForInteger(),
            'Variant.IsTransactionType must return false (enum collapses to NavOption; indistinguishable)');
    end;

    // ── WideChar ──────────────────────────────────────────────────────────────

    [Test]
    procedure VSI_IsWideChar_False_IsNoOp()
    begin
        Assert.IsFalse(Src.IsWideChar_ForInteger(),
            'Variant.IsWideChar must return false (WideChar not Variant-assignable in BC AL; use IsChar)');
    end;
}
