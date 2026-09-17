// CodeunitPublisherMetadataResolutionTests — issue #4218.
//
// WHAT #4218 MEASURED
//   A CODEUNIT-published event subscription never resolved its publisher, so the
//   Event Subscription virtual table (2000000140) reported it Active=No,
//   Error Information=ErrorOriginalApplicationObjectNotFound and Event Type=Business,
//   while a TABLE-published one in the same run read Active=Yes / Trigger / empty.
//
//   BC's own NavEventSubscription ctor resolves the publisher through
//     GetOriginalApplicationObject -> NCLMetadata.TryGetMetaApplicationObject(objectType, id, ...)
//       -> NCLMetadata.GetMetaApplicationObject(ApplicationObjectId, bool, int)
//         -> RecordPatches.NCLMetadata_GetMetaApplicationObjectByType   (the Cecil-rewritten body)
//   and returns early on null, leaving originalEventAttribute unassigned; OriginalEventType
//   then falls back to NavEventType.Business. That dispatch had branches for Table, Report,
//   Page, Query, PermissionSet and XmlPort, and none for CodeUnit — so every codeunit
//   publisher took the early return. Business was BC's default for "publishing method not
//   found", not a misprojection of the event type.
//
// WHAT THIS TEST PROVES, AND WHAT IT DELIBERATELY DOES NOT
//   This is a RUNNER-MECHANISM test. The AL-observable claim — that a subscriber on a
//   codeunit-published [IntegrationEvent] reads Event Type::Integration and Active — is a
//   statement about BC, so it is proven upstream by al-language corpus codeunit 60955
//   "Test Event Subscription VT", arm EventSubscription_Row_DescribesTheSubscription, which
//   is green on a real service tier (corpus PR 374). Duplicating that claim here would only
//   prove the runner agrees with itself.
//
//   What this file pins instead is the join the runner owns: which CLR type the runner hands
//   BC for a given AL codeunit id, and that an id it cannot resolve stays a loud not-found.
//
//   * POSITIVE — a loaded Codeunit{id} type reaches the CodeUnit branch of the dispatch, and
//     the meta handed back carries that exact type as ApplicationObjectClrType and (CodeUnit,
//     id) as ApplicationObjectId. ApplicationObjectClrType is the load-bearing one: BC reads
//     it and does its OWN reflection over the AL-emitted attribute
//     (NavEventPublisherReflectionHelper.GetScopeType, then GetMethodInfoByScopeType(...)
//     .Attribute), so the event type BC ends up reporting comes from the publisher's real
//     [IntegrationEvent], never from anything the runner decides.
//
//   * NEGATIVE — the lookup is WIDENED, not abolished. An id with no loadable Codeunit{id}
//     type still raises BC's own NavMetadataNotFoundException, which is the type
//     NCLMetadata.TryGetMetaApplicationObject catches in order to answer false. Handing back
//     a skeleton for anything anyone asks about would turn every genuinely-missing codeunit
//     into a silent success and put ErrorOriginalApplicationObjectNotFound permanently out of
//     reach — the runner would then be fabricating the very answer #4218 says it is not.
//
//   The fixture type is declared ABSTRACT on purpose. FindCodeunitType requires only
//   NavCodeunit.IsAssignableFrom, and nothing on this path instantiates the type, so an
//   abstract subclass needs no override of NavCodeunit's ~9 inherited abstract members —
//   which keeps the test from going red when a future BC adds one.

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AlRunner.Patches;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CodeunitPublisherMetadataResolutionTests
{
    private readonly BcEngineFixture _engine;

    public CodeunitPublisherMetadataResolutionTests(BcEngineFixture engine)
    {
        _engine = engine;
    }

    /// <summary>
    /// Compile and load an assembly declaring <c>Codeunit{id}</c> as an abstract subclass of
    /// <c>NavCodeunit</c> — the shape <c>FindCodeunitType</c> scans loaded assemblies for.
    /// Referencing the live Ncl assembly rather than a path on disk keeps the emitted type
    /// identical to the one the engine already mapped.
    /// </summary>
    private static Assembly CompileAndLoadCodeunit(int codeunitId)
    {
        var navCodeunit = typeof(Microsoft.Dynamics.Nav.Runtime.NavCodeunit);
        var source = $@"
public abstract class Codeunit{codeunitId} : Microsoft.Dynamics.Nav.Runtime.NavCodeunit
{{
    protected Codeunit{codeunitId}(Microsoft.Dynamics.Nav.Runtime.ITreeObject owner, int id)
        : base(owner, id) {{ }}
}}";
        var refs = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
            .Select(a => MetadataReference.CreateFromFile(a.Location))
            .ToList();

        var compilation = CSharpCompilation.Create(
            $"al-runner-test-codeunit-{codeunitId}-{Guid.NewGuid():N}",
            new[] { CSharpSyntaxTree.ParseText(source) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
        Assert.NotNull(navCodeunit);
        return Assembly.Load(ms.ToArray());
    }

    [SkippableFact]
    public void CodeunitDispatch_ResolvesTheMetaAndCarriesTheAlEmittedClrType()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // 61731 is inside the fixture id band and is declared by no registered .app in this run.
        //
        // Deliberately NOT probed before the load. CodeunitPatches._codeunitTypeCache memoizes
        // a MISS as well as a hit, so an EnsureCodeunitMetaById(61731) here would cache null
        // and this test would then fail against a correct fix — measured while writing it.
        // Nothing is lost: Assert.Same below pins the meta to the exact type compiled in this
        // test, so a pre-existing foreign Codeunit61731 would fail rather than pass for free.
        const int codeunitId = 61731;

        var asm = CompileAndLoadCodeunit(codeunitId);
        var expected = asm.GetType($"Codeunit{codeunitId}");
        Assert.NotNull(expected);

        var meta = RecordPatches.NCLMetadata_GetMetaApplicationObjectByType(
            self: null!, ObjectType.CodeUnit, codeunitId, requireCompiled: true, emitVersion: 0);

        Assert.NotNull(meta);

        // The load-bearing assertion: BC reads ApplicationObjectClrType off this meta and
        // reflects over it to find the publisher's own NavEventAttribute.
        var clrType = meta.GetType()
            .GetProperty("ApplicationObjectClrType",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(meta) as Type;
        Assert.Same(expected, clrType);

        // ...and it identifies itself as that codeunit, which is what BC folds into
        // appQualifiedApplicationObjectId.
        var objectId = meta.GetType()
            .GetProperty("ApplicationObjectId",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(meta)!;
        Assert.Equal(ObjectType.CodeUnit,
            objectId.GetType().GetProperty("ObjectType")!.GetValue(objectId));
        Assert.Equal(codeunitId,
            objectId.GetType().GetProperty("ObjectNumber")!.GetValue(objectId));
    }

    [SkippableFact]
    public void CodeunitDispatch_UnresolvableId_StillRaisesBcsOwnNotFound()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // No Codeunit65959 type is loaded by anything in this run, so the branch must fall
        // through to the throw rather than hand out a skeleton. NavMetadataNotFoundException
        // specifically: NCLMetadata.TryGetMetaApplicationObject catches that type (and
        // NavNCLArgumentException) and nothing else, so a foreign exception type would turn
        // every optional publisher probe into a hard failure instead of a false answer.
        Assert.Throws<NavMetadataNotFoundException>(() =>
            RecordPatches.NCLMetadata_GetMetaApplicationObjectByType(
                self: null!, ObjectType.CodeUnit, 65959, requireCompiled: true, emitVersion: 0));
    }

    [SkippableFact]
    public void EnsureCodeunitMetaById_RefusesNonPositiveIds_WithoutTouchingReflection()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // NavCodeunit.ObjectId is not always populated on test-scope receivers, so 0 is a real
        // value that reaches this path; it is "I could not tell which codeunit", never
        // codeunit zero. Answering null keeps it on the not-found path.
        Assert.Null(AlRunner.BcRuntime.EnsureCodeunitMetaById(0));
        Assert.Null(AlRunner.BcRuntime.EnsureCodeunitMetaById(-1));
    }
}
