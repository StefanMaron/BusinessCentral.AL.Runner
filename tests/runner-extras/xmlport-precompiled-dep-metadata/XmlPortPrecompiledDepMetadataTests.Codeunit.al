// Regression test — #3510: an xmlport in a precompiled dependency has no
// NCLMetaXmlPort built for it, so its own constructor throws.
//
// HERMETIC BY CONSTRUCTION. "XPD Precompiled XmlPort Dep" (this folder's
// .alpackages/*.app + .deps-bin/*.dll, and app.json's dependency entry) ships
// xmlport 61602 as a Tier-1 precompiled DLL: DependencyLoader.LoadOne finds
// .deps-bin/AL_Runner_Fixtures_XPD_Precompiled_XmlPort_Dep_1.0.0.0.dll and
// Assembly.Load()s it directly, never extracting or compiling its AL source.
// That is the same shape a real Microsoft Base Application xmlport has, without
// this suite needing an externally-provisioned Base Application.
//
// RED (before the fix): BuildNCLMetaXmlPort's first line was
//     if (!_parsedXmlPorts.TryGetValue(xmlPortId, out var parsed)) return null;
// and _parsedXmlPorts is filled ONLY by RecordPatches.AlXmlPortParser, from AL
// source the runner itself parses. A precompiled dependency's xmlport is never
// in it, so the builder returned null BEFORE entering its try block — which is
// why #3777's now-unfiltered catch printed nothing for this failure: there was
// no exception to surface. The null reached
// NCLMetadata_GetMetaApplicationObjectByType and became:
//     NavMetadataNotFoundException: The metadata object XmlPort 61602 was not found.
//       at NavXmlPort.BeginInitialization()
//       at XmlPort61602..ctor(ITreeObject parent, NCLMetaXmlPort metadata)
// That is a .NET exception BC does not model as an AL error, so it is NOT
// catchable with asserterror or [TryFunction] — it aborts the whole test with a
// raw stack. That is what makes the RED run unambiguous.
//
// GREEN (after the fix): the existence check is KnownXmlPortIdSet(), which — like
// KnownReportIdSet() for the report builder, the exact same gap fixed for reports
// earlier — also counts xmlports declared by a registered dependency .app and
// xmlports present as a compiled XmlPort{id} type in a loaded assembly. The
// skeleton NCLMetaXmlPort is then built and the ctor completes.
//
// POPULATION (the finding, measured, not reasoned): against a real Microsoft Base
// Application on BC 28.1.49838.53910, 44 of 44 precompiled xmlports AL can see
// failed this way — every one, with the identical NavMetadataNotFoundException.
// The six ids in #3510 are the ones Microsoft's own test buckets happened to
// reach, not a distinguishable subset.
codeunit 65941 "XPD Tests"
{
    Subtype = Test;

    var
        Assert: Codeunit "XPD Assert";

    // Positive: constructing an xmlport that lives entirely in a Tier-1 precompiled
    // dependency must get PAST the metadata-object lookup. The claim is a boundary, not an
    // end-to-end export, and the boundary is the whole point: #3510 dies asking NCLMetadata
    // whether XmlPort 61602 exists at all, before any node schema is wanted. The runner
    // deriving no node schema for a precompiled xmlport is a SEPARATE, still-open gap
    // (#3797), so this suite asserts the first boundary and declares the second.
    //
    // The two failures are told apart by their type, which is what makes this test
    // discriminating rather than a bare no-throw:
    //
    //   RED   NavMetadataNotFoundException — a .NET exception BC does not model as an AL
    //         error, so it is NOT catchable by asserterror and aborts the whole test with a
    //         raw C# stack. That is why the RED run cannot reach this test's assertions at
    //         all, and why it cannot be confused with a controlled refusal.
    //   GREEN RunnerOutOfScopeException, reason not-yet-implemented, naming
    //         INCLObjectXmlMetadataLoader.GetMetaObjectXmlMetadata(XmlPort 61602) — a
    //         controlled, typed, loud refusal one layer deeper, raised only AFTER the
    //         NCLMetaXmlPort was successfully built and BeginInitialization proceeded to ask
    //         for the node schema.
    //
    // Declared expect-oos in tests/expectations/oos-xmlport-precompiled.json, so the runner
    // classifies the GREEN outcome as a pass and the manifest — not a comment — is what
    // fails loudly in both directions if this boundary ever moves again.
    [Test]
    procedure PrecompiledDepXmlPort_MetadataObjectBuilds_SchemaStillOutOfScope()
    var
        XpdPort: XmlPort "XPDDep Export Port";
        BlobRec: Record "XPD Blob Holder";
        OutStr: OutStream;
    begin
        BlobRec.Init();
        BlobRec."Entry No." := 1;
        BlobRec."Blob Data".CreateOutStream(OutStr);

        // RED: throws NavMetadataNotFoundException from inside XmlPort61602..ctor and the
        // test aborts here. GREEN: the ctor completes, BeginInitialization proceeds, and the
        // out-of-scope signal below is raised for the node schema instead.
        XpdPort.SetDestination(OutStr);
        XpdPort.Export();
    end;

    // Negative: an id no app declares must still fail loudly, so the fix cannot
    // be "hand out a skeleton for anything anyone asks about" — that would turn
    // every genuinely-missing object into a silent success, which is the failure
    // mode KnownXmlPortIdSet() exists to avoid. Reached through the STATIC form
    // because AL cannot name an xmlport no app declares; that form is gated
    // earlier, by BC's own compiled-object-id check, so this pins the
    // AL-observable contract rather than the builder directly. The builder's own
    // negative — KnownXmlPortIdSet() must not contain an undeclared id — is
    // AlRunner.Tests/XmlPortSkeletonMetadataTests.cs.
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
