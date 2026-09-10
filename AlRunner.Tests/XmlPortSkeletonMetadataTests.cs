// XmlPortSkeletonMetadataTests — issue #3510.
//
// WHAT #3510 MEASURED
//   An xmlport living in a precompiled dependency threw NavMetadataNotFoundException from
//   inside its OWN constructor:
//     NavXmlPort.BeginInitialization -> NCLMetadata.GetMetaXmlPortById
//       -> RecordPatches.NCLMetadata_GetMetaApplicationObjectByType (the throw)
//   The dispatch site's two-step fallback ends in BuildNCLMetaXmlPort, whose first line was
//   an existence check against _parsedXmlPorts — the dictionary RecordPatches.AlXmlPortParser
//   fills from AL SOURCE THE RUNNER ITSELF PARSES. A precompiled dependency's xmlport is
//   never in it, so the builder returned null before entering its try block. That is also why
//   #3777's now-unfiltered catch printed nothing for this failure: no exception was ever
//   thrown to surface.
//
// THE POPULATION (the finding, measured rather than reasoned)
//   Against a real Microsoft Base Application on BC 28.1.49838.53910, all 44 precompiled
//   xmlports AL can see through AllObj failed identically — the whole population, not the six
//   ids #3510 observed through Microsoft's test buckets. Answering the issue's own open
//   question: it was never those six, and the skeleton fallback had never worked for a
//   precompiled xmlport at all.
//
// THE FIX
//   KnownXmlPortIdSet() (RecordPatches.KnownXmlPortIds.cs) replaces that check, mirroring
//   KnownReportIdSet() — which was widened for the identical gap on reports — with the same
//   three sources: source-parsed ids, ids declared by a registered dependency .app, and ids
//   present as a compiled XmlPort{id} type in a loaded assembly.
//
// WHAT THIS TEST PROVES, AND WHAT IT DELIBERATELY DOES NOT
//   This is a RUNNER-MECHANISM test. The AL-observable claim — that an xmlport in a Tier-1
//   precompiled dependency gets past the metadata-object lookup — is proven by the AL suite
//   tests/runner-extras/xmlport-precompiled-dep-metadata, against a hermetic precompiled
//   dependency. This file pins the id-set mechanism underneath it, in milliseconds:
//
//   * POSITIVE — a compiled XmlPort{id} type in a newly loaded assembly reaches
//     KnownXmlPortIdSet(), the set both BuildNCLMetaXmlPort and PopulateNclMetadataCache read.
//   * NEGATIVE — the existence check is WIDENED, never abolished. Decoy types that merely
//     start with "XmlPort" but carry a non-numeric suffix, or do not start with it at all,
//     contribute nothing; and an id no source states is absent from the set. This is the
//     property that stops the fix degrading into "hand out a skeleton for anything anyone
//     asks about", which would turn every genuinely-missing object into a silent success.
//   * PARSE GATE — TryParseXmlPortId's exact boundary, including the id <= 0 case, asserted
//     through the same public path rather than by re-implementing the rule here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AlRunner.Patches;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class XmlPortSkeletonMetadataTests
{
    private readonly BcEngineFixture _engine;

    public XmlPortSkeletonMetadataTests(BcEngineFixture engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Compiles a tiny in-memory assembly holding exactly the given (empty) public types and
    /// loads it with Assembly.Load(byte[]) — the same shape DependencyLoader uses for a
    /// Tier-1 precompiled dependency, so the discovery path is exercised as production
    /// exercises it.
    /// </summary>
    private static Assembly CompileAndLoad(string assemblyName, params string[] typeNames)
    {
        var source = string.Join("\n", typeNames.Select(n => $"public class {n} {{ }}"));
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
        return Assembly.Load(ms.ToArray());
    }

    [SkippableFact]
    public void CompiledXmlPortType_ReachesKnownXmlPortIdSet_AndDecoysContributeNothing()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // Snapshot before this test's assembly exists, to diff against afterwards.
        var before = new HashSet<int>(RecordPatches.KnownXmlPortIdSet());

        // 61698 is inside the fixture id band and is NOT declared by any registered .app in
        // this run, so before the load it cannot already be present — asserted below rather
        // than assumed.
        Assert.DoesNotContain(61698, before);

        CompileAndLoad(
            $"al-runner-test-xmlport-ids-{Guid.NewGuid():N}",
            "XmlPort61698",       // valid — must be discovered
            "NotAnXmlPortType",   // does not start with "XmlPort" at all
            "XmlPortABC",         // starts with "XmlPort", non-numeric suffix
            "XmlPort0",           // numeric but not a positive id
            "XmlPortManagement"); // the real BC helper-type shape the gate must exclude

        var after = RecordPatches.KnownXmlPortIdSet();

        // POSITIVE: the compiled type's id reaches the set both BuildNCLMetaXmlPort and
        // PopulateNclMetadataCache read.
        Assert.Contains(61698, after);

        // NEGATIVE: exactly one id was newly contributed. A looser "starts with XmlPort"
        // match would have smuggled in a bogus id or thrown on "ABC"; this proves it does
        // neither, and that "XmlPort0" is refused because the gate requires id > 0.
        var added = new HashSet<int>(after);
        added.ExceptWith(before);
        Assert.Equal(new HashSet<int> { 61698 }, added);
    }

    [SkippableFact]
    public void KnownXmlPortIdSet_DoesNotContainAnIdNoSourceStates()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // The check is widened, not abolished: an id that no AL source parsed, no registered
        // dependency .app declares, and no loaded assembly carries as a compiled XmlPort{id}
        // type must stay absent — so BuildNCLMetaXmlPort still returns null for it and the
        // dispatch site still raises BC's own NavMetadataNotFoundException, which is what
        // NCLMetadata.TryGetMetaApplicationObject needs in order to answer "false" rather
        // than failing hard.
        var ids = RecordPatches.KnownXmlPortIdSet();

        Assert.DoesNotContain(65939, ids);
        Assert.DoesNotContain(int.MaxValue, ids);
    }
}
