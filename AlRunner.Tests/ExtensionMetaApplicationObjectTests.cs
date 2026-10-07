// ExtensionMetaApplicationObjectTests — issue #5384.
//
// Code coverage resolves the metadata of every object whose method scope it enters
// (ALCodeEnvironment.GetOrAssignScopeId -> NCLMetadata.GetMetaApplicationObject), and the runner's
// replacement body had no branch for a tableextension, pageextension or reportextension.
//
// RUNNER-MECHANISM test. That a procedure declared in a tableextension runs while recording is a
// statement about BC, proven upstream by the al-language corpus codeunit "Test Code Cov Table
// Extension"; the pageextension and reportextension arms and the cascade are in
// tests/runner-extras/code-coverage-extension-metadata. What this pins is the join the runner owns:
// a loaded CLR type of the right kind resolves to a meta identifying itself as (kind, id), one
// object per (kind, id), and an id nothing declares stays BC's own not-found.
//
// The fixture types are ABSTRACT: only the name and the base-type chain are read, so no override
// of the inherited abstract members is needed.

using System;
using System.Linq;
using System.Reflection;
using AlRunner.Patches;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Dynamics.Nav.Types;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class ExtensionMetaApplicationObjectTests
{
    private readonly BcEngineFixture _engine;

    public ExtensionMetaApplicationObjectTests(BcEngineFixture engine) => _engine = engine;

    private static void CompileAndLoad(string source)
    {
        var refs = LoadedAssemblyReferences.Build();
        var compilation = CSharpCompilation.Create(
            $"al-runner-test-extension-meta-{Guid.NewGuid():N}",
            new[] { CSharpSyntaxTree.ParseText(source) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new System.IO.MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
        Assembly.Load(ms.ToArray());
    }

    private static (ObjectType Type, int Number) IdentityOf(object meta)
    {
        var id = meta.GetType()
            .GetProperty("ApplicationObjectId", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(meta)!;
        return ((ObjectType)id.GetType().GetProperty("ObjectType")!.GetValue(id)!,
            (int)id.GetType().GetProperty("ObjectNumber")!.GetValue(id)!);
    }

    [SkippableTheory]
    [InlineData(ObjectType.TableExtension, 61741)]
    [InlineData(ObjectType.PageExtension, 61742)]
    [InlineData(ObjectType.ReportExtension, 61743)]
    public void LoadedExtensionType_ResolvesToAMetaOfItsOwnKindAndId_OncePerObject(ObjectType kind, int id)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        var (clrBase, ctorArgs) = kind switch
        {
            ObjectType.TableExtension => ("Microsoft.Dynamics.Nav.Runtime.Extensions.NavRecordExtension", "parent, id"),
            ObjectType.PageExtension => ("Microsoft.Dynamics.Nav.Runtime.Extensions.NavFormExtension", "parent, id, null, null"),
            _ => ("Microsoft.Dynamics.Nav.Runtime.Extensions.NavReportExtension", "parent, id"),
        };
        CompileAndLoad($@"
public abstract class {kind}{id} : {clrBase}
{{
    protected {kind}{id}(Microsoft.Dynamics.Nav.Runtime.ITreeObject parent, int id) : base({ctorArgs}) {{ }}
}}");

        var meta = RecordPatches.NCLMetadata_GetMetaApplicationObjectByType(
            self: null!, kind, id, requireCompiled: true, emitVersion: 0);

        Assert.NotNull(meta);
        Assert.Equal((kind, id), IdentityOf(meta));
        // One object per (kind, id): it owns the MethodNumberCounter BC keeps for the object's lifetime.
        Assert.Same(meta, RecordPatches.NCLMetadata_GetMetaApplicationObjectByType(
            self: null!, kind, id, requireCompiled: true, emitVersion: 0));
    }

    [SkippableFact]
    public void ExtensionsOfDifferentKindsSharingOneId_GetSeparateObjects()
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // AL numbers each object kind separately, so a tableextension, a pageextension and a
        // reportextension may all be 61744. One object per (kind, id) means each reads its own kind;
        // an id-only key would hand the first-resolved kind's object to the other two.
        const int id = 61744;
        CompileAndLoad($@"
public abstract class TableExtension{id} : Microsoft.Dynamics.Nav.Runtime.Extensions.NavRecordExtension
{{
    protected TableExtension{id}(Microsoft.Dynamics.Nav.Runtime.ITreeObject parent, int id) : base(parent, id) {{ }}
}}
public abstract class PageExtension{id} : Microsoft.Dynamics.Nav.Runtime.Extensions.NavFormExtension
{{
    protected PageExtension{id}(Microsoft.Dynamics.Nav.Runtime.ITreeObject parent, int id) : base(parent, id, null, null) {{ }}
}}
public abstract class ReportExtension{id} : Microsoft.Dynamics.Nav.Runtime.Extensions.NavReportExtension
{{
    protected ReportExtension{id}(Microsoft.Dynamics.Nav.Runtime.ITreeObject parent, int id) : base(parent, id) {{ }}
}}");

        var kinds = new[] { ObjectType.TableExtension, ObjectType.PageExtension, ObjectType.ReportExtension };
        var metas = kinds.Select(k => RecordPatches.NCLMetadata_GetMetaApplicationObjectByType(
            self: null!, k, id, requireCompiled: true, emitVersion: 0)).ToArray();

        for (var i = 0; i < kinds.Length; i++)
            Assert.Equal((kinds[i], id), IdentityOf(metas[i]));
        Assert.Equal(3, metas.Distinct().Count());
    }

    [SkippableTheory]
    [InlineData(ObjectType.TableExtension)]
    [InlineData(ObjectType.PageExtension)]
    [InlineData(ObjectType.ReportExtension)]
    public void ExtensionIdNothingDeclares_StillRaisesBcsOwnNotFound(ObjectType kind)
    {
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

        // NavMetadataNotFoundException is what NCLMetadata.TryGetMetaApplicationObject catches to
        // answer false; handing back a meta for any id would make a missing extension read as present.
        Assert.Throws<NavMetadataNotFoundException>(() =>
            RecordPatches.NCLMetadata_GetMetaApplicationObjectByType(
                self: null!, kind, 65969, requireCompiled: true, emitVersion: 0));
    }
}
