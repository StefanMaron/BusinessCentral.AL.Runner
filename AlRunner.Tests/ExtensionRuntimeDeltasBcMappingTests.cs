// ExtensionRuntimeDeltasBcMappingTests — the drift test for the AL-area → BC-container table in
// RecordPatches.ExtensionRuntimeDeltasMetadataEquivalence (#3923).
//
// The table is a literal, so that the render takes no load-time dependency on
// Microsoft.Dynamics.Nav.CodeAnalysis. A literal copied out of BC can stop matching BC with
// nothing failing, which is what this fixes: it asks BC's OWN emitter method for every member of
// both enums and fails when the runner's answer differs. A BC version that adds an area, renames
// a container or changes a mapping fails here rather than silently in the metadata-equivalence
// harness on one leg.

using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class ExtensionRuntimeDeltasBcMappingTests
{
    /// <summary>
    /// BC's own <c>MetadataEmitterHelper.GetContainerType</c> for one enum, as
    /// (AL area name → container name); areas the method refuses are omitted, because they are
    /// not containers.
    /// </summary>
    private static IReadOnlyDictionary<string, string>? BcMapping(string enumTypeName)
    {
        var dll = Path.Combine(AlRunner.Infrastructure.BcArtifacts.ServiceTierDir,
            "Microsoft.Dynamics.Nav.CodeAnalysis.dll");
        if (!File.Exists(dll)) return null;

        var ca = Assembly.LoadFrom(dll);
        var helper = ca.GetType("Microsoft.Dynamics.Nav.CodeAnalysis.Emit.MetadataEmitterHelper");
        var areaEnum = ca.GetType("Microsoft.Dynamics.Nav.CodeAnalysis.Symbols." + enumTypeName);
        if (helper is null || areaEnum is null) return null;

        var method = helper
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "GetContainerType"
                                 && m.GetParameters() is { Length: 1 } p
                                 && p[0].ParameterType == areaEnum
                                 && m.ReturnType == typeof(string));
        if (method is null) return null;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in Enum.GetNames(areaEnum))
        {
            // An area BC refuses is not a container. Caught rather than skipped by name, so a
            // version that starts or stops refusing one is visible as a table difference.
            try
            {
                if (method.Invoke(null, new object?[] { Enum.Parse(areaEnum, name) }) is string s)
                    map[name] = s;
            }
            catch (TargetInvocationException) { }
        }
        return map;
    }

    /// <summary>
    /// Every area BC maps to a container, the runner maps to the SAME container — and every area
    /// BC refuses, the runner refuses too.
    /// </summary>
    [SkippableTheory]
    [InlineData("ActionAreaKind", true)]
    [InlineData("AreaKind", false)]
    public void The_runners_area_table_agrees_with_BCs_own_emitter(string enumTypeName, bool isAction)
    {
        var bc = BcMapping(enumTypeName);
        Skip.If(bc is null,
            $"Microsoft.Dynamics.Nav.CodeAnalysis.dll, its {enumTypeName} enum or " +
            "MetadataEmitterHelper.GetContainerType is not reachable on this box, so there is no " +
            "oracle to compare against and this test would assert nothing.");

        // Not an empty dictionary quietly passing: both enums have members BC maps, so a zero
        // here means the reflection found the method and it answered for nothing — a broken
        // measurement wearing the shape of agreement.
        Assert.NotEmpty(bc!);

        var runner = new Dictionary<string, string>(StringComparer.Ordinal);
        var ca = Assembly.LoadFrom(Path.Combine(
            AlRunner.Infrastructure.BcArtifacts.ServiceTierDir, "Microsoft.Dynamics.Nav.CodeAnalysis.dll"));
        foreach (var name in Enum.GetNames(
                     ca.GetType("Microsoft.Dynamics.Nav.CodeAnalysis.Symbols." + enumTypeName)!))
            if (RecordPatches.BcContainerForAlAreaForTests(name, isAction) is { } c)
                runner[name] = c;

        Assert.Equal(
            bc!.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray(),
            runner.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToArray());
    }

    /// <summary>
    /// The runner's pageextension <c>ControlGUID</c> is what BC's own
    /// <c>MetadataEmitterHelper.GeneratePageControlGuidString(controlId, objectId,
    /// SymbolKind.PageExtension)</c> answers, over ids chosen to reach every byte of the encoding:
    /// both 16-bit halves, the high byte's sign bit, and 0, which BC special-cases.
    /// </summary>
    [SkippableTheory]
    [InlineData(2515, 1174679510)]
    [InlineData(774, 191117080)]
    [InlineData(9862, 2032512200)]
    [InlineData(2147483647, -1)]
    [InlineData(1, int.MinValue)]
    [InlineData(324, 0)]
    public void The_runners_ControlGUID_agrees_with_BCs_own_emitter(int extensionId, int memberId)
    {
        var dll = Path.Combine(AlRunner.Infrastructure.BcArtifacts.ServiceTierDir,
            "Microsoft.Dynamics.Nav.CodeAnalysis.dll");
        Skip.IfNot(File.Exists(dll),
            $"Microsoft.Dynamics.Nav.CodeAnalysis.dll is not on this box ({dll}), so there is no " +
            "oracle to compare against and this test would assert nothing.");

        var ca = Assembly.LoadFrom(dll);
        var kind = ca.GetType("Microsoft.Dynamics.Nav.CodeAnalysis.SymbolKind", throwOnError: true)!;
        var method = ca.GetType("Microsoft.Dynamics.Nav.CodeAnalysis.Emit.MetadataEmitterHelper", throwOnError: true)!
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Single(m => m.Name == "GeneratePageControlGuidString");

        // Every parameter supplied explicitly: MethodInfo.Invoke does not apply C# defaults.
        var bc = (string)method.Invoke(null, new object?[]
            { memberId, extensionId, Enum.Parse(kind, "PageExtension"), "B" })!;

        Assert.Equal(bc, RecordPatches.PageExtensionControlGuidForTests(extensionId, memberId));
    }

    /// <summary>
    /// A word from neither vocabulary — a sibling member's name — is NOT a container, on either
    /// side. That is what keeps an anchored change on the kind-implied fallback instead of
    /// emitting an attribute BC's reader would refuse.
    /// </summary>
    [Theory]
    [InlineData("Refresh")]
    [InlineData("Has SUPER permission set")]
    [InlineData("63ca2fa4-4f03-4f2b-a480-172fef340d3f_ActiveUsers")]
    // The RUNTIME container spellings are not AL area names either: an anchor can never be one,
    // so passing one through would be the render inventing a case BC does not produce.
    [InlineData("RelatedInformation")]
    [InlineData("ActionItems")]
    [InlineData("ContentArea")]
    [InlineData("")]
    public void A_word_that_is_not_an_AL_area_name_is_not_a_container(string anchor)
    {
        Assert.Null(RecordPatches.BcContainerForAlAreaForTests(anchor, isAction: true));
        Assert.Null(RecordPatches.BcContainerForAlAreaForTests(anchor, isAction: false));
    }
}
