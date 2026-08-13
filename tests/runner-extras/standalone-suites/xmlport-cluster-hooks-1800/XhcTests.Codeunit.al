/// Backing table for the xmlport under test — a real stored table so the control
/// experiment does not depend on any virtual-table provider.
table 62180 "XHC Row"
{
    DataClassification = CustomerContent;

    fields
    {
        field(1; "Entry No."; Integer) { }
        field(2; Name; Text[50]) { }
    }

    keys
    {
        key(PK; "Entry No.") { Clustered = true; }
    }
}

/// Minimal xmlport bound to XHC Row — the target of the #1800 orphaned-JmpHook cluster.
///
/// The #1800 audit found EIGHT orphaned JmpHook registrations on NavXmlPort
/// (BeginInitialization/EndInitialization/Add(TableNode|FieldNode|TextNode)/Export/
/// Import/Run(0-arg instance)/SetTableView/RunXmlPort) — JmpHook is disabled by
/// default, so none of them ever fired, and BC's real, unpatched bodies ran instead.
/// An earlier revision of this fix Cecil-owned BeginInitialization to install stub
/// metadata, believing Session.MetadataProvider is null on the skeleton and NREs the
/// ctor — that was a misdiagnosis (AlRunner/Patches/MetadataPatches.cs's
/// InjectSkeletonSystemTenant already seeds session.tenant/systemTenant for exactly
/// this call path) and an active regression: it broke 14 previously-passing
/// al-language corpus tests (Codeunit60206/60207). Once reverted, the pristine,
/// unpatched behaviour was confirmed empirically: construction succeeds and a full
/// SetTableView → Export → Import round trip completes with no throw at all, with
/// ZERO runner intervention on any of those eight methods. So the runner-mechanism
/// claim these tests below (InstanceConstruction_DoesNotThrow,
/// InstanceExportImportRoundTrip_RealBcBody_NoThrow) exist to prove is a REGRESSION
/// GUARD, not a fix: the runner must never again install a redirect on this cluster.
/// Full round-trip correctness (actual XML shape, row filtering, field values) is
/// plain BC behaviour and is proven upstream in the corpus, not re-proven here (see
/// bc-behavior-tests-go-upstream.md).
///
/// The ONE genuine, permanent out-of-scope surface in this cluster, and the actual #1800
/// fix landed by this PR, is the four static XmlPort.Run(int[, bool[, bool[, NavRecord]]])
/// overloads (see StaticRun1_UnresolvableId_ThrowsOutOfScope /
/// StaticRun1_KnownId_ThrowsOutOfScope / StaticRun3_Import_ThrowsOutOfScope /
/// StaticRun4_WithRecord_ThrowsOutOfScope below). Decompiling BC's real, unpatched Ncl.dll
/// body shows every overload's RunXmlPort() unconditionally calls
/// NavFile.InternalUpload/InternalDownload with displayDialog:true — a client-callback file
/// browse dialog the record/args can never bypass, since `record` only ever feeds
/// SetTableView (a row filter), never the I/O stream. That is docs/scope.md#file-storage's
/// "browser round-trip" surface, the same bucket as NavFile.ALUpload/ALDownload (see
/// AlRunner/Patches/FilePatches.cs) — a typed RunnerOutOfScopeException Cecil redirect, not
/// a no-op and not a "BC's real body is already correct, delete the hook" case like the
/// eight methods above.
xmlport 62181 "XHC Port"
{
    Direction = Both;
    UseRequestPage = false;

    schema
    {
        textelement(root)
        {
            tableelement(Row_; "XHC Row")
            {
                XmlName = 'Row';
                fieldelement(EntryNo; Row_."Entry No.") { }
                fieldelement(RowName; Row_.Name) { }
            }
        }
    }
}

codeunit 62182 "XHC Tests"
{
    Subtype = Test;

    trigger OnRun()
    begin
    end;

    // ── Construction: BeginInitialization/EndInitialization/Add(*Node) scaffolding ──
    // If the ctor-time hooks are orphaned, this NREs before any test body runs at all —
    // i.e. it fails as an uncaught runtime error, not as an AL assertion. Wrapping
    // construction itself in asserterror would hide that distinction, so this test's
    // claim is narrower and non-negotiable: construction completes and yields a live
    // object whose static Run() overload is reachable (see below).
    [Test]
    procedure InstanceConstruction_DoesNotThrow()
    var
        Xhc: XmlPort "XHC Port";
    begin
        Clear(Xhc);
    end;

    // ── XMLPORT.RUN(id[, requestWindow[, import[, record]]]) static overloads — a genuine,
    // permanent out-of-scope surface (docs/scope.md#file-storage), NOT a safe no-op: BC's
    // real, unpatched body always attempts a client-callback file browse dialog
    // (NavFile.InternalUpload/InternalDownload), which the runner's non-interactive
    // skeleton session cannot satisfy. The Cecil redirect in NclCecilRewrite.cs replaces the
    // whole method body before BC's own NCLMetadata id lookup runs, so the throw fires
    // unconditionally — for a resolvable id exactly like an unresolvable one — rather than
    // silently succeeding or returning a default. Both tests below assert the *effect*
    // (the specific OOS signal), not merely "does not throw" — a gutted implementation that
    // always returned without throwing would fail both.
    [Test]
    procedure StaticRun1_UnresolvableId_ThrowsOutOfScope()
    begin
        // An id the runner's metadata cache never learns about — proves the throw is
        // unconditional (not merely a lucky match against a real, resolvable id).
        asserterror XmlPort.Run(999999999);

        if StrPos(GetLastErrorText(), 'out-of-scope: NavXmlPort.Run') = 0 then
            Error('Expected an out-of-scope error naming NavXmlPort.Run, got: %1', GetLastErrorText());
        if StrPos(GetLastErrorText(), 'browser-roundtrip') = 0 then
            Error('Expected the browser-roundtrip reason, got: %1', GetLastErrorText());
    end;

    [Test]
    procedure StaticRun1_KnownId_ThrowsOutOfScope()
    begin
        asserterror XmlPort.Run(62181);

        if StrPos(GetLastErrorText(), 'out-of-scope: NavXmlPort.Run') = 0 then
            Error('Expected an out-of-scope error naming NavXmlPort.Run, got: %1', GetLastErrorText());
        if StrPos(GetLastErrorText(), 'browser-roundtrip') = 0 then
            Error('Expected the browser-roundtrip reason, got: %1', GetLastErrorText());
    end;

    // Overload 3 (requestWindow, import — no record) and overload 4 (+ record) proven
    // separately: the record parameter only ever feeds SetTableView, never the I/O target,
    // so it must NOT change the outcome — both still throw the same OOS signal.
    [Test]
    procedure StaticRun3_Import_ThrowsOutOfScope()
    begin
        asserterror XmlPort.Run(62181, false, true);

        if StrPos(GetLastErrorText(), 'out-of-scope: NavXmlPort.Run') = 0 then
            Error('Expected an out-of-scope error naming NavXmlPort.Run, got: %1', GetLastErrorText());
        if StrPos(GetLastErrorText(), 'browser-roundtrip') = 0 then
            Error('Expected the browser-roundtrip reason, got: %1', GetLastErrorText());
    end;

    [Test]
    procedure StaticRun4_WithRecord_ThrowsOutOfScope()
    var
        RowFilter: Record "XHC Row";
    begin
        RowFilter.SetRange("Entry No.", 1);
        asserterror XmlPort.Run(62181, false, true, RowFilter);

        if StrPos(GetLastErrorText(), 'out-of-scope: NavXmlPort.Run') = 0 then
            Error('Expected an out-of-scope error naming NavXmlPort.Run, got: %1', GetLastErrorText());
        if StrPos(GetLastErrorText(), 'browser-roundtrip') = 0 then
            Error('Expected the browser-roundtrip reason, got: %1', GetLastErrorText());
    end;

    // ── Instance Export/SetTableView/Import — real BC body, reached end-to-end. ──
    // Earlier revisions of this suite asserted these four calls must throw
    // RunnerOutOfScopeException("not-yet-implemented"), and a still-earlier revision of
    // the runner fix believed construction itself needed a stub-metadata Cecil redirect.
    // Both premises turned out to be wrong: BC's own real, UNPATCHED bodies for
    // construction and for Export/SetTableView/Import all handle well-formed usage
    // correctly on the skeleton (proven both empirically against a pristine build and by
    // the full al-language corpus — Codeunit60206/60207: nested-table export/import,
    // SetTableView row filtering, auto-update/auto-replace, all passing against the
    // unpatched precompiled body). Re-asserting that same correctness here would just be
    // a runner-local restatement of a BC-behaviour claim the corpus already owns (see
    // bc-behavior-tests-go-upstream.md). This test's actual claim is narrower and purely
    // a regression guard: a correctly-set-up instance completes a real
    // SetTableView → Export → Import round trip without throwing anything at all, with
    // no runner redirect installed anywhere on this cluster.
    [Test]
    procedure InstanceExportImportRoundTrip_RealBcBody_NoThrow()
    var
        Row_: Record "XHC Row";
        RowFilter: Record "XHC Row";
        TempBlob: Codeunit "Temp Blob";
        XhcOut: XmlPort "XHC Port";
        XhcIn: XmlPort "XHC Port";
        DocumentOutStream: OutStream;
        DocumentInStream: InStream;
        Ok: Boolean;
    begin
        Row_.Init();
        Row_."Entry No." := 1;
        Row_.Name := 'First';
        Row_.Insert();

        TempBlob.CreateOutStream(DocumentOutStream);
        RowFilter.SetRange("Entry No.", 1);
        XhcOut.SetTableView(RowFilter);
        XhcOut.SetDestination(DocumentOutStream);
        Ok := XhcOut.Export();
        if not Ok then
            Error('XHC Port.Export() reported failure against a correctly-set-up OutStream destination.');

        // Delete the source row before import — the exported XML would otherwise re-import
        // a row whose primary key already exists, which is a legitimate duplicate-key
        // failure, not evidence about the orphaned-hook fix this test exists to prove.
        Row_.Delete();

        TempBlob.CreateInStream(DocumentInStream);
        XhcIn.SetSource(DocumentInStream);
        Ok := XhcIn.Import();
        if not Ok then
            Error('XHC Port.Import() reported failure against a correctly-set-up InStream source.');
    end;
}
