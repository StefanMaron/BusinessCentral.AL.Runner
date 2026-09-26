// Regression tests for two boundaries on ONE precompiled-dependency xmlport.
//
// HERMETIC BY CONSTRUCTION. "XPD Precompiled XmlPort Dep" (this folder's
// .alpackages/*.app + .deps-bin/*.dll, and app.json's dependency entry) ships
// xmlport 61602 as a Tier-1 precompiled DLL: DependencyLoader.LoadOne finds
// .deps-bin/AL_Runner_Fixtures_XPD_Precompiled_XmlPort_Dep_1.0.0.0.dll and
// Assembly.Load()s it directly, never extracting or compiling its AL source.
// That is the same shape a real Microsoft Base Application xmlport has, without
// this suite needing an externally-provisioned Base Application.
//
// #3510 (LANDED) — BuildNCLMetaXmlPort's existence check was _parsedXmlPorts, the
// AL-SOURCE parser's dictionary, so it returned null for every xmlport the runner
// did not compile from source and the port's own ctor died with
// NavMetadataNotFoundException — a .NET exception BC does not model as an AL error,
// so it is not catchable with asserterror and aborts the whole test.
//
// #3797 (THIS SUITE'S SUBJECT) — after #3510 the metadata object built and
// BeginInitialization proceeded one layer deeper, to ask for the xmlport's NODE
// SCHEMA, which the runner did not derive: all 44 precompiled xmlports AL can see
// then refused with RunnerOutOfScopeException on
// INCLObjectXmlMetadataLoader.GetMetaObjectXmlMetadata, reason not-yet-implemented.
// That refusal was declared in tests/expectations/oos-xmlport-precompiled.json,
// which THIS PR removes because the surface is now implemented.
//
// WHERE THE SCHEMA COMES FROM. Not SymbolReference.json: measured on BC
// 28.1.49838.53910, its Base Application symbol file states an xmlport's Properties
// and Variables and NO node tree at all. It comes from the AL source the .app
// embeds under src/, parsed with BC's own AL parser — see
// AlRunner/Patches/DependencyXmlPortMetadata.cs. All 40 Base Application xmlports
// state a ReferenceSourceFileName and all 40 of those files are present in the .app.
//
// WHY THE ASSERTIONS READ THE EXPORTED BYTES. A test that only checked "Export()
// did not throw" would pass against a document with an EMPTY node schema — which is
// precisely the wrong answer the removed expectation entry's Note warned about, a
// port that silently exports nothing. So the positive test reads the exported bytes
// and names every node the schema must contain.
//
// WHAT THIS SUITE DELIBERATELY DOES NOT ASSERT, AND WHERE THAT LIVES INSTEAD. It does
// not seed rows and assert their VALUES reach the output. Doing so needs a persisted
// Insert(), and the export then runs against whatever the company holds — which makes
// the suite depend on the install/persistence closure rather than on the node schema
// this issue is about. The value-binding half is proved in C# instead, by
// AlRunner.Tests/DependencyXmlPortMetadataTests.cs, which asserts the emitted document
// carries SourceTable/SourceField/DataType for the bound nodes: a port whose bindings
// were wrong emits different values THERE, deterministically and without a company.
// The two halves together are the claim; neither alone is.
codeunit 65941 "XPD Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "XPD Assert";

    // Positive: an xmlport living entirely in a Tier-1 precompiled dependency exports
    // through BC's own XmlPort engine, against a node schema the runner reconstructed
    // from the .app.
    //
    // The assertions are on the exported XML because that is the only AL-observable that
    // distinguishes a real schema from an empty one. ALL FOUR nodes the AL declares must
    // appear — <Root> (textelement), <Header> (the tableelement under its XmlName), and
    // <No>/<Description> (the two fieldelements). Before this PR the whole call refused
    // with RunnerOutOfScopeException; a partially-derived schema would drop one of the
    // four rather than all of them, which naming each one individually is what catches.
    [Test]
    procedure PrecompiledDepXmlPort_ExportsAgainstDerivedNodeSchema()
    var
        XpdPort: XmlPort "XPDDep Export Port";
        // A sink for the exported bytes, never the port's source. Temporary on purpose: the
        // export writes to the stream this hands out, and nothing about the claim needs the
        // blob to survive the test.
        TempBlob: Record "XPD Blob Holder" temporary;
        OutStr: OutStream;
        InStr: InStream;
        Exported: Text;
        Line: Text;
    begin
        TempBlob."Blob Data".CreateOutStream(OutStr);

        // RED before this PR: RunnerOutOfScopeException from
        // GetMetaObjectXmlMetadata(XmlPort 61602), reason not-yet-implemented — the
        // metadata object built (#3510) and BeginInitialization then asked for the node
        // schema the runner did not derive.
        XpdPort.SetDestination(OutStr);
        XpdPort.Export();

        TempBlob."Blob Data".CreateInStream(InStr);
        while not InStr.EOS() do begin
            InStr.ReadText(Line);
            Exported += Line;
        end;

        Assert.Contains(Exported, '<Root',
            'the textelement node must reach the output — an empty node schema exports nothing (#3797)');
        Assert.Contains(Exported, '<Header',
            'the tableelement node must reach the output under its XmlName — this is also what proves XmlName was read as an AL STRING literal: passing its quotes through made BC''s exporter refuse the element name outright');
        Assert.Contains(Exported, '<No',
            'the fieldelement bound to "No." must reach the output as a derived node');
        Assert.Contains(Exported, '<Description',
            'the fieldelement bound to Description must reach the output as a derived node');
    end;

    // Negative, same surface: an id no app declares must still fail loudly, so the fix
    // cannot be "hand out a document for anything anyone asks about" — that would turn
    // every genuinely-missing object into a silent success, which is the failure mode
    // KnownXmlPortIdSet() exists to avoid. Reached through the STATIC form because AL
    // cannot name an xmlport no app declares; that form is gated earlier, by BC's own
    // compiled-object-id check, so this pins the AL-observable contract rather than the
    // builder directly. The builder's own negative — KnownXmlPortIdSet() must not
    // contain an undeclared id — is AlRunner.Tests/XmlPortSkeletonMetadataTests.cs, and
    // the "declared but no recoverable schema still refuses" negative is
    // AlRunner.Tests/DependencyXmlPortMetadataTests.cs.
    [Test]
    procedure UnknownXmlPortId_StillThrowsNotFound()
    var
        BlobRec: Record "XPD Blob Holder";
        OutStr: OutStream;
    begin
        BlobRec."Blob Data".CreateOutStream(OutStr);
        asserterror XmlPort.Export(65959, OutStr);
        Assert.Contains(GetLastErrorText(), '65959',
            'an xmlport id that no registered app declares must still raise a real error naming the id — the fix must widen the EXISTENCE set, never abolish the existence check');
    end;
}
