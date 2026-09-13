// DesignerProfileCopyRefusalTests — AlRunner#2324. Runner-mechanism tests; the BC claim
// (CopyProfile creates a tenant-owned copy) is corpus codeunit 60908, profile/TestCopyProfile.al.
//
// Two things the corpus cannot see:
//   * NavDesignerALFunctions.CopyProfile refuses by name at its ENTRY. BC's body swallows any
//     throw inside the copy into Base Application's generic "could not be copied", so a refusal
//     anywhere deeper would ship looking like a fix and name nothing.
//   * The skeleton NavSession carries every STRING value NavSession's constructor assigns by
//     field initializer. GetUninitializedObject skips them; a null AbbreviatedLanguageName is
//     what broke the designer copy first (SyntaxFactory.Identifier(null) NRE).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class DesignerProfileCopyRefusalTests
{
    private readonly BcEngineFixture _engine;

    public DesignerProfileCopyRefusalTests(BcEngineFixture engine) => _engine = engine;

    private void SkipUnlessEngineReady() =>
        TestArtifacts.SkipIf(!_engine.Ready,
            _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    private static Microsoft.Dynamics.Nav.Runtime.NavSession Skeleton()
    {
        // Same trigger SkeletonFormatSettingsWarmTests uses to make sure the runtime is up.
        _ = Microsoft.Dynamics.Nav.Runtime.NavDate.Create(
            DateTime.SpecifyKind(new DateTime(2026, 1, 15), DateTimeKind.Local));
        var session = BcRuntime.SkeletonSession as Microsoft.Dynamics.Nav.Runtime.NavSession;
        Assert.NotNull(session);
        return session!;
    }

    [Fact]
    public void CopyProfile_OnTheRewrittenNcl_RefusesByNameInsteadOfReturningAFailedResponse()
    {
        SkipUnlessEngineReady();
        _ = Skeleton();

        var functions = typeof(Microsoft.Dynamics.Nav.Runtime.NavSession).Assembly.GetType(
            "Microsoft.Dynamics.Nav.Runtime.Designer.NavDesignerALFunctions", throwOnError: true)!;
        var copyProfile = functions.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == "CopyProfile" && m.GetParameters().Length == 4);

        var thrown = Assert.Throws<TargetInvocationException>(() =>
            copyProfile.Invoke(null, new object[] { "ALT SOURCE", Guid.Empty, "ALT TARGET", "Caption" }));

        var refusal = Assert.IsType<RunnerOutOfScopeException>(thrown.InnerException);
        Assert.Equal("NavDesignerALFunctions.CopyProfile", refusal.Api);
        Assert.StartsWith("not-yet-implemented", refusal.Reason);
        Assert.Contains("#4124", refusal.Reason);
    }

    [Fact]
    public void SkeletonSession_AbbreviatedLanguageName_IsTheConstructorsEnu()
    {
        SkipUnlessEngineReady();
        Assert.Equal("ENU", Skeleton().AbbreviatedLanguageName);
    }

    // Reads NavSession's own constructor, so a string initializer a later BC version adds is
    // caught here rather than as an NRE somewhere downstream.
    [Fact]
    public void SkeletonSession_HoldsEveryStringFieldInitializerOfNavSessionsConstructor()
    {
        SkipUnlessEngineReady();
        var session = Skeleton();
        var sessionType = typeof(Microsoft.Dynamics.Nav.Runtime.NavSession);

        using var ncl = AssemblyDefinition.ReadAssembly(sessionType.Assembly.Location);
        var ctor = ncl.MainModule.GetType(sessionType.FullName).Methods
            .Where(m => m.IsConstructor && !m.IsStatic && m.HasBody)
            .OrderBy(m => m.Parameters.Count)
            .First();

        var initializers = new List<(string Field, string Value)>();
        var ins = ctor.Body.Instructions;
        for (var i = 1; i < ins.Count; i++)
        {
            if (ins[i].OpCode == OpCodes.Call && ins[i].Operand is MethodReference { Name: ".ctor" })
                break; // the base-constructor call ends the field-initializer prologue
            if (ins[i].OpCode != OpCodes.Stfld || ins[i].Operand is not FieldReference f
                || f.FieldType.FullName != "System.String") continue;
            var load = ins[i - 1];
            if (load.OpCode == OpCodes.Ldstr)
                initializers.Add((f.Name, (string)load.Operand));
            else if (load.OpCode == OpCodes.Ldsfld && load.Operand is FieldReference { Name: "Empty" } e
                     && e.DeclaringType.FullName == "System.String")
                initializers.Add((f.Name, string.Empty));
        }

        Assert.Contains(initializers, x => x.Field == "<AbbreviatedLanguageName>k__BackingField");

        var wrong = initializers
            .Select(x => (x.Field, x.Value, Actual: sessionType
                .GetField(x.Field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
                ?.GetValue(session) as string))
            .Where(x => x.Actual != x.Value)
            .Select(x => $"{x.Field}: constructor assigns \"{x.Value}\", skeleton holds "
                         + (x.Actual is null ? "null" : $"\"{x.Actual}\""))
            .ToList();
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
