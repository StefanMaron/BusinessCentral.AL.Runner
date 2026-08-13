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
/// NavXmlPort.BeginInitialization/EndInitialization/Add(*Node)/ctor are ctor-time
/// scaffolding whose ONLY job is to let this object construct without NREing on the
/// null skeleton Session.MetadataProvider (see AlRunner/Patches/XmlPortPatches.cs).
/// NavXmlPort.Export/Import/Run(0-arg instance)/SetTableView are the loud-failure guards:
/// in-memory xmlport serialization is genuinely not implemented, so they must raise
/// RunnerOutOfScopeException("not-yet-implemented") — never silently succeed or NRE.
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

    // ── XMLPORT.RUN(id) static overloads — safe no-ops in standalone mode (no request
    // page, no interactive I/O target) — must NOT throw, and must NOT be a silent
    // no-op standing in for something that actually executed. Just prove they return
    // control to the caller instead of NREing on the ctor-time NCLMetadata lookup that
    // BC's real, unpatched body performs for every test-assembly xmlport id.
    [Test]
    procedure StaticRun_UnresolvableId_DoesNotThrow()
    begin
        // An id the runner's metadata cache never learns about — proves the no-op is
        // unconditional (not merely a lucky match against a real, resolvable id).
        XmlPort.Run(999999999);
    end;

    [Test]
    procedure StaticRun_KnownId_DoesNotThrow()
    begin
        XmlPort.Run(62181);
    end;

    // ── Instance Export/Import/Run()/SetTableView — the loud not-yet-implemented guards.
    // Positive claim: each raises RunnerOutOfScopeException naming the surface and the
    // not-yet-implemented reason — not a generic NullReferenceException from BC's own
    // unpatched body reaching into the null skeleton session, and not a silent success.
    [Test]
    procedure InstanceRun_ThrowsOutOfScope_NotSilentNoOp_NotRawNRE()
    var
        Xhc: XmlPort "XHC Port";
    begin
        asserterror Xhc.Run();

        if StrPos(GetLastErrorText(), 'not-yet-implemented') = 0 then
            Error('Expected the not-yet-implemented reason, got: %1', GetLastErrorText());
        if StrPos(GetLastErrorText(), 'NavXmlPort.Run') = 0 then
            Error('Expected the error to name NavXmlPort.Run, got: %1', GetLastErrorText());
    end;

    [Test]
    procedure InstanceExport_ThrowsOutOfScope_NotSilentNoOp_NotRawNRE()
    var
        Xhc: XmlPort "XHC Port";
    begin
        asserterror Xhc.Export();

        if StrPos(GetLastErrorText(), 'not-yet-implemented') = 0 then
            Error('Expected the not-yet-implemented reason, got: %1', GetLastErrorText());
        if StrPos(GetLastErrorText(), 'NavXmlPort.Export') = 0 then
            Error('Expected the error to name NavXmlPort.Export, got: %1', GetLastErrorText());
    end;

    [Test]
    procedure InstanceImport_ThrowsOutOfScope_NotSilentNoOp_NotRawNRE()
    var
        Xhc: XmlPort "XHC Port";
    begin
        asserterror Xhc.Import();

        if StrPos(GetLastErrorText(), 'not-yet-implemented') = 0 then
            Error('Expected the not-yet-implemented reason, got: %1', GetLastErrorText());
        if StrPos(GetLastErrorText(), 'NavXmlPort.Import') = 0 then
            Error('Expected the error to name NavXmlPort.Import, got: %1', GetLastErrorText());
    end;

    [Test]
    procedure InstanceSetTableView_ThrowsOutOfScope_NotSilentNoOp_NotRawNRE()
    var
        Xhc: XmlPort "XHC Port";
        Row_: Record "XHC Row";
    begin
        asserterror Xhc.SetTableView(Row_);

        if StrPos(GetLastErrorText(), 'not-yet-implemented') = 0 then
            Error('Expected the not-yet-implemented reason, got: %1', GetLastErrorText());
        if StrPos(GetLastErrorText(), 'NavXmlPort.SetTableView') = 0 then
            Error('Expected the error to name NavXmlPort.SetTableView, got: %1', GetLastErrorText());
    end;
}
