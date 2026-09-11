// PermissionSetFromBcDocumentTests — #3609. A permission set the runner COMPILED gets its
// declaration from BC's own emitted <PermissionSet> document instead of from the regex
// derivation over AL source text, and one with no document keeps the derivation.
//
// WHAT IS PROVED HERE AND WHAT IS PROVED UPSTREAM
// -----------------------------------------------
// The BC-observable half — that "Metadata Permission Set" (2000000250) reports a
// source-declared set's Role ID / Name / Assignable, and that its tabledata grants survive
// into the permission metadata layer — is plain BC behaviour and is adjudicated on a real
// service tier by the corpus PR named in this change's PR body
// (.claude/rules/bc-behavior-tests-go-upstream.md).
//
// These tests pin the runner-only half underneath it, which no service tier can answer,
// because each is a question about THIS runner build's own plumbing:
//
//   1. The reader transcribes BC's document faithfully — the exact ids, ordinals and mask
//      values BC emitted, and the ENU text out of CaptionML.
//   2. The tabledata grant that the AL-source route DROPS SILENTLY survives this one. That is
//      the RED: ResolveSourcePermissionEntries maps PermissionObject ordinal 0/1 to null
//      through AlKeywordForPermissionObject and then `continue`s BEFORE its own diagnostic,
//      so the grant vanished at every verbosity including AL_RUNNER_DIAG_PERMMETA=1.
//   3. ExcludedPermissionSets is carried, not hardcoded null, when BC states it.
//   4. A permission set with no registered document keeps the derivation rather than
//      becoming an empty row — the compile-cache-HIT direction.

using System;
using System.IO;
using AlRunner;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Serialized against every other test touching this static registry — see
// ObjectMetadataRegistrySerialCollection — and cleared both ends, so a document registered
// here cannot leak into a sibling's not-found branch.
[Collection("object-metadata-registry")]
public class PermissionSetFromBcDocumentTests : IDisposable
{
    public PermissionSetFromBcDocumentTests() => AlObjectMetadataRegistry.Clear();
    public void Dispose() => AlObjectMetadataRegistry.Clear();

    // Ids outside every fixture, dependency .app and corpus range in this repository, so a
    // document registered here cannot collide with one a sibling test registered.
    private const int SetWithEverything = 77452001;
    private const int SetIncluded = 77452002;
    private const int SetExcluded = 77452003;
    private const int SetWithNoDocument = 77452004;

    /// <summary>
    /// BC 28.1's real output shape for
    /// <c>permissionset … { Access = Internal; Caption = '…'; IncludedPermissionSets = …;
    /// ExcludedPermissionSets = …; Permissions = tabledata "PP Thing" = RIMD, codeunit … = X; }</c>,
    /// copied from an AL_RUNNER_TRACE_OBJECT_METADATA=2 dump rather than hand-written — the
    /// namespace, the "1"/"0" Assignable spelling and the "ENU=" caption prefix are all load
    /// bearing and a hand-written approximation would not have them.
    /// </summary>
    private static string DocumentWithEverything() =>
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <PermissionSet MetadataVersion="130000" ID="{SetWithEverything}" Name="PSD Everything" ALNamespace=""
                        Access="Internal" Assignable="1" CaptionML="ENU=PSD everything caption"
                        IncludedPermissionSets="{SetIncluded}" ExcludedPermissionSets="{SetExcluded}"
                        xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
           <Permissions>
             <Permission Type="0" ID="70700" Value="15" />
             <Permission Type="5" ID="70703" Value="16" />
           </Permissions>
         </PermissionSet>
         """;

    [Fact]
    public void ReadsIdsOrdinalsAndMasksExactlyAsBcStatesThem()
    {
        AlObjectMetadataRegistry.Register(
            RecordPatches.BcPermissionSetMetadataKind, SetWithEverything, "PSD Everything",
            DocumentWithEverything());

        var set = RecordPatches.TryReadPermissionSetFromBcDocument(SetWithEverything, "PSD Everything");

        Assert.NotNull(set);
        Assert.Equal(SetWithEverything, set!.Id);
        Assert.Equal("PSD Everything", set.Name);
        // The ENU text alone, not the raw "ENU=…" multi-language string.
        Assert.Equal("PSD everything caption", set.Caption);
        Assert.True(set.Assignable);
        Assert.Equal("Internal", set.Access);

        // THE POINT OF THE CONVERSION: the tabledata grant. Ordinal 0 is PermissionObject
        // tabledata, and the AL-source route cannot express it at all.
        Assert.NotNull(set.Permissions);
        var tableData = Assert.Single(set.Permissions!, p => p.ObjectType == 0);
        Assert.Equal(70700, tableData.ObjectId);
        // RIMD = R1 + I2 + M4 + D8. Concrete, not "non-zero".
        Assert.Equal(15, tableData.Value);

        var codeunit = Assert.Single(set.Permissions!, p => p.ObjectType == 5);
        Assert.Equal(70703, codeunit.ObjectId);
        Assert.Equal(16, codeunit.Value);   // X = Execute

        // Include/exclude edges arrive as resolved OBJECT IDS, so nothing has to survive a
        // name lookup. The name list stays null on this route by construction.
        Assert.Null(set.IncludedPermissionSets);
        Assert.Equal(new[] { SetIncluded }, set.IncludedPermissionSetIds);
        Assert.Equal(new[] { SetExcluded }, set.ExcludedPermissionSetIds);
    }

    /// <summary>
    /// The mask encoding is BC's, and the lowercase AL letters are the INDIRECT variants —
    /// <c>Rimd</c> is 1 + 64 + 128 + 256 = 449, not 15. Pinned because the derivation
    /// reimplemented this table by hand (MaskFromAlLetters) and a future reader comparing the
    /// two needs the agreement to be asserted rather than assumed.
    /// </summary>
    [Fact]
    public void CarriesBcsIndirectMaskValueUnchanged()
    {
        const int id = SetWithEverything + 100;
        AlObjectMetadataRegistry.Register(
            RecordPatches.BcPermissionSetMetadataKind, id, "PSD Indirect",
            $"""
             <PermissionSet ID="{id}" Name="PSD Indirect" Assignable="1"
                            xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
               <Permissions><Permission Type="0" ID="70700" Value="449" /></Permissions>
             </PermissionSet>
             """);

        var set = RecordPatches.TryReadPermissionSetFromBcDocument(id, "PSD Indirect");

        Assert.Equal(449, Assert.Single(set!.Permissions!).Value);
    }

    /// <summary>
    /// <c>Assignable="0"</c> and an ABSENT <c>Assignable</c> both read as false, and
    /// <c>Assignable="1"</c> reads as true — so a reader that ignored the attribute entirely
    /// fails the third assertion rather than passing by accident (#2417, #3806).
    ///
    /// <para>CLAIM: this matches BC's own reader on the identical document.
    /// <c>MetaPermissionSet.Create</c> assigns the property only inside
    /// <c>case 10: if (name == "Assignable")</c>, and the constructor never touches it, so an
    /// absent attribute leaves <c>default(bool)</c> — false. Measured on
    /// Microsoft.Dynamics.Nav.Types.dll 28.1.49838.53910 (sha256 c91ede8f…).</para>
    ///
    /// <para>TRAP: this test previously asserted <c>true</c> here, reasoning from AL's
    /// source-language default. That default governs what the COMPILER emits, not what BC's
    /// reader does with a document that states nothing — and BC's emitter does omit the
    /// attribute (set 68 of the 178 #3806 measured).</para>
    /// </summary>
    [Fact]
    public void AssignableFalseAndAbsentBothReadAsFalse_AndAnExplicitTrueIsHonored()
    {
        const int notAssignable = SetWithEverything + 200;
        const int unstated = SetWithEverything + 201;
        const int assignable = SetWithEverything + 202;

        AlObjectMetadataRegistry.Register(
            RecordPatches.BcPermissionSetMetadataKind, notAssignable, "PSD NotAssignable",
            $"""
             <PermissionSet ID="{notAssignable}" Name="PSD NotAssignable" Assignable="0"
                            xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects" />
             """);
        AlObjectMetadataRegistry.Register(
            RecordPatches.BcPermissionSetMetadataKind, unstated, "PSD Unstated",
            $"""
             <PermissionSet ID="{unstated}" Name="PSD Unstated"
                            xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects" />
             """);
        AlObjectMetadataRegistry.Register(
            RecordPatches.BcPermissionSetMetadataKind, assignable, "PSD Assignable",
            $"""
             <PermissionSet ID="{assignable}" Name="PSD Assignable" Assignable="1"
                            xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects" />
             """);

        Assert.False(RecordPatches.TryReadPermissionSetFromBcDocument(notAssignable, "PSD NotAssignable")!.Assignable);
        Assert.False(RecordPatches.TryReadPermissionSetFromBcDocument(unstated, "PSD Unstated")!.Assignable);
        Assert.True(RecordPatches.TryReadPermissionSetFromBcDocument(assignable, "PSD Assignable")!.Assignable);
    }

    /// <summary>
    /// A set with no registered document answers null, so <c>EnumerateKnownPermissionSets</c>
    /// keeps the AL-source derivation. This is the compile-cache-HIT direction: Emit runs only
    /// on a MISS, and a warm run whose replay supplied no document must keep the row rather
    /// than lose every source-declared permission set.
    /// </summary>
    [Fact]
    public void NoDocumentAnswersNullSoTheDerivationSurvives()
    {
        Assert.Null(RecordPatches.TryReadPermissionSetFromBcDocument(SetWithNoDocument, "PSD Absent"));
        Assert.False(RecordPatches.HasBcPermissionSetDocument(SetWithNoDocument));
    }

    /// <summary>
    /// A document that arrived and will not parse is a shape change in BC's emitter. It must
    /// refuse loudly rather than fall back to the derivation — a weaker answer substituted on
    /// error is wrong metadata under a green build (.claude/rules/loud-failures.md).
    /// </summary>
    [Fact]
    public void AMalformedDocumentRefusesInsteadOfFallingBackToTheDerivation()
    {
        const int malformed = SetWithEverything + 300;
        AlObjectMetadataRegistry.Register(
            RecordPatches.BcPermissionSetMetadataKind, malformed, "PSD Malformed",
            "<PermissionSet ID=\"" + malformed + "\"><Permissions>");

        var ex = Assert.ThrowsAny<System.Exception>(
            () => RecordPatches.TryReadPermissionSetFromBcDocument(malformed, "PSD Malformed"));

        // The refusal has to name the surface, or it cannot be acted on.
        Assert.Contains(malformed.ToString(), ex.Message);
        Assert.Contains("PermissionSet", ex.Message);
    }
}

/// <summary>
/// #3609's wiring: what <c>EnumerateKnownPermissionSets</c> actually composes for a
/// source-declared set, through the same <c>ComposeSourcePermissionSet</c> seam it calls.
///
/// <para>This is the RED. The AL-source derivation cannot express a <c>tabledata</c> grant:
/// <c>ParsedObjectDecls</c> carries no tables, so <c>AlKeywordForPermissionObject</c> answers
/// null for PermissionObject ordinals 0 and 1, and <c>ResolveSourcePermissionEntries</c>
/// <c>continue</c>s BEFORE the diagnostic that would have reported the loss. A permission set
/// whose only grant is on a table therefore reached BC's permission metadata layer with an
/// EMPTY Permissions list, silently, at every verbosity — and the composed set below is
/// asserted to carry the grant with BC's own concrete id and mask instead.</para>
/// </summary>
[Collection("object-metadata-registry")]
public class PermissionSetFromBcDocumentWiringTests : IDisposable
{
    public PermissionSetFromBcDocumentWiringTests() => AlObjectMetadataRegistry.Clear();
    public void Dispose() => AlObjectMetadataRegistry.Clear();

    private const int SetId = 77452500;
    private const int TableId = 77452501;

    /// <summary>The AL source shape the fixture declares:
    /// <c>Permissions = tabledata "PSD Thing" = RIMD;</c>, i.e. one entry naming a TABLE,
    /// which is exactly the shape the derivation drops.</summary>
    private static RecordPatches.ParsedAlPermissionSet SourceDeclarationWithATableDataGrant() =>
        new(SetId, "PSD Wiring", "PSD wiring caption", Assignable: true,
            AppId: Guid.Empty, AppName: "PSD App",
            Permissions: new[] { new RecordPatches.ParsedAlPermissionEntry(0, "PSD Thing", 15) },
            IncludedPermissionSets: null,
            Access: null);

    [Fact]
    public void WithoutBcsDocument_TheTableDataGrantIsDroppedSilently()
    {
        // No document registered — the compile-cache-HIT direction, and the state every
        // source-compiled permission set was in before #3609.
        var composed = RecordPatches.ComposeSourcePermissionSet(SourceDeclarationWithATableDataGrant());

        Assert.Equal(SetId, composed.Id);
        // The grant is gone. Not "an empty list is fine" — this is the defect, pinned so the
        // GREEN below is measured against it rather than asserted in prose.
        Assert.Empty(composed.Permissions!);
    }

    [Fact]
    public void WithBcsDocument_TheTableDataGrantSurvivesWithBcsOwnIdAndMask()
    {
        AlObjectMetadataRegistry.Register(
            RecordPatches.BcPermissionSetMetadataKind, SetId, "PSD Wiring",
            $"""
             <PermissionSet ID="{SetId}" Name="PSD Wiring" Assignable="1"
                            CaptionML="ENU=PSD wiring caption"
                            xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
               <Permissions><Permission Type="0" ID="{TableId}" Value="15" /></Permissions>
             </PermissionSet>
             """);

        var composed = RecordPatches.ComposeSourcePermissionSet(SourceDeclarationWithATableDataGrant());

        var grant = Assert.Single(composed.Permissions!);
        Assert.Equal(0, grant.ObjectType);          // tabledata
        Assert.Equal(TableId, grant.ObjectId);      // the id AL source never states
        Assert.Equal(15, grant.Value);              // RIMD
    }

}

/// <summary>
/// #3609's own lesson applied to itself: every drop inside the document reader must be
/// OBSERVABLE. The defect this change fixed was a grant discarded by a <c>continue</c> placed
/// above its own diagnostic, so it vanished at every verbosity — a reader that silently skips
/// a malformed row would reintroduce exactly that, one layer up.
///
/// <para>These tests assert the diagnostic text on stderr under
/// <c>AL_RUNNER_DIAG_PERMMETA=1</c>, not merely that the row was dropped. Dropping is already
/// covered by the value assertions elsewhere in this file; what is pinned here is that the loss
/// leaves a trace someone can grep for.</para>
/// </summary>
[Collection("object-metadata-registry")]
public class PermissionSetFromBcDocumentDiagnosticTests : IDisposable
{
    private readonly string? _priorDiag;

    public PermissionSetFromBcDocumentDiagnosticTests()
    {
        AlObjectMetadataRegistry.Clear();
        _priorDiag = Environment.GetEnvironmentVariable("AL_RUNNER_DIAG_PERMMETA");
        Environment.SetEnvironmentVariable("AL_RUNNER_DIAG_PERMMETA", "1");
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("AL_RUNNER_DIAG_PERMMETA", _priorDiag);
        AlObjectMetadataRegistry.Clear();
    }

    private const int MalformedPermission = 77452700;
    private const int MalformedIncludeList = 77452701;

    private static string CaptureStderr(Action body)
    {
        var prior = Console.Error;
        var sw = new StringWriter();
        Console.SetError(sw);
        try { body(); } finally { Console.SetError(prior); }
        return sw.ToString();
    }

    [Fact]
    public void AnUnreadablePermissionRowIsDropped_AndSaysSo()
    {
        AlObjectMetadataRegistry.Register(
            RecordPatches.BcPermissionSetMetadataKind, MalformedPermission, "PSD BadRow",
            $"""
             <PermissionSet ID="{MalformedPermission}" Name="PSD BadRow" Assignable="1"
                            xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects">
               <Permissions>
                 <Permission Type="0" ID="70700" Value="15" />
                 <Permission Type="0" ID="not-an-id" Value="15" />
               </Permissions>
             </PermissionSet>
             """);

        BcAppSymbolCache.PermissionSetSymbol? set = null;
        var stderr = CaptureStderr(
            () => set = RecordPatches.TryReadPermissionSetFromBcDocument(MalformedPermission, "PSD BadRow"));

        // The good row survives; only the unreadable one is dropped.
        var kept = Assert.Single(set!.Permissions!);
        Assert.Equal(70700, kept.ObjectId);

        // And the loss is greppable, carrying the offending value so it can be acted on.
        Assert.Contains("[perm-metadata]", stderr, StringComparison.Ordinal);
        Assert.Contains("not-an-id", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnreadableIncludeEdgeIsDropped_AndSaysSo()
    {
        AlObjectMetadataRegistry.Register(
            RecordPatches.BcPermissionSetMetadataKind, MalformedIncludeList, "PSD BadEdge",
            $"""
             <PermissionSet ID="{MalformedIncludeList}" Name="PSD BadEdge" Assignable="1"
                            IncludedPermissionSets="70701,SUPER"
                            xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects" />
             """);

        BcAppSymbolCache.PermissionSetSymbol? set = null;
        var stderr = CaptureStderr(
            () => set = RecordPatches.TryReadPermissionSetFromBcDocument(MalformedIncludeList, "PSD BadEdge"));

        Assert.Equal(new[] { 70701 }, set!.IncludedPermissionSetIds);
        Assert.Contains("IncludedPermissionSets", stderr, StringComparison.Ordinal);
        Assert.Contains("SUPER", stderr, StringComparison.Ordinal);
    }

    /// <summary>
    /// A trailing separator is a formatting artifact, not a lost edge — it must NOT produce a
    /// diagnostic, or the channel fills with noise and a real loss stops standing out.
    /// </summary>
    [Fact]
    public void ATrailingSeparatorIsNotReportedAsALoss()
    {
        const int id = MalformedIncludeList + 10;
        AlObjectMetadataRegistry.Register(
            RecordPatches.BcPermissionSetMetadataKind, id, "PSD Trailing",
            $"""
             <PermissionSet ID="{id}" Name="PSD Trailing" Assignable="1"
                            IncludedPermissionSets="70701,"
                            xmlns="urn:schemas-microsoft-com:dynamics:NAV:MetaObjects" />
             """);

        BcAppSymbolCache.PermissionSetSymbol? set = null;
        var stderr = CaptureStderr(
            () => set = RecordPatches.TryReadPermissionSetFromBcDocument(id, "PSD Trailing"));

        Assert.Equal(new[] { 70701 }, set!.IncludedPermissionSetIds);
        Assert.DoesNotContain("[perm-metadata]", stderr, StringComparison.Ordinal);
    }
}
