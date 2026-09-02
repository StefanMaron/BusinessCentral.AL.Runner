using System.Linq;
using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pins the boundary that keeps `Microsoft.Dynamics.Nav.CodeAnalysis` out of type
/// resolution on Program.cs's argument-handling path.
///
/// `RadObjectIdentity` is a record STRUCT whose first field is a `NavCA.SymbolKind`.
/// Because it is a value type, the CLR needs its exact layout to JIT any method that
/// merely names it in a signature, and getting that layout means loading BC's
/// CodeAnalysis assembly. On a cold artifact cache that assembly is not resolvable yet,
/// so naming the struct from Program.cs made `provision` — and every other
/// artifact-cache path — die with an unhandled FileNotFoundException and no managed
/// stack, long before it could print its own provisioning message.
///
/// Measured on one worktree with identical build commands: 20 of the 22 provisioning
/// tests in this project failed with `RadObjectIdentity` named in Program.cs, and all 22
/// passed once the boundary was projected to `AffectedObjectId` instead. Those 22 tests
/// are the end-to-end proof; the two assertions here are the narrow guard that says why,
/// so a reintroduction fails with a message naming the cause instead of an assembly-load
/// crash somewhere else.
/// </summary>
public sealed class ProgramMustNotNameNavCaValueTypesTests
{
    private static Assembly RunnerAssembly => typeof(AlRunner.Infrastructure.BcArtifacts).Assembly;

    [Fact]
    public void AffectedObjectId_CarriesNoFieldFromBcsCodeAnalysisAssembly()
    {
        var t = RunnerAssembly.GetType("AlRunner.AffectedObjectId", throwOnError: false);
        Assert.NotNull(t);

        var offenders = t!.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(f => (f.FieldType.Assembly.GetName().Name ?? "")
                .StartsWith("Microsoft.Dynamics.Nav", System.StringComparison.Ordinal))
            .Select(f => $"{f.Name}: {f.FieldType.FullName}")
            .ToList();

        Assert.True(offenders.Count == 0,
            "AffectedObjectId exists precisely so the affected-selection boundary carries no BC " +
            "compiler type. A field from a Microsoft.Dynamics.Nav assembly reintroduces the cold-cache " +
            "provisioning crash. Offending fields: " + string.Join(", ", offenders));
    }

    [Fact]
    public void ProgramNamesNoNavCaBearingValueTypeInAnyMemberSignature()
    {
        var program = RunnerAssembly.GetTypes().FirstOrDefault(x => x.Name == "Program");
        Assert.NotNull(program);

        static bool CarriesNavCa(System.Type t)
        {
            if (!t.IsValueType) return false;
            foreach (var arg in t.IsGenericType ? t.GetGenericArguments() : System.Type.EmptyTypes)
                if (CarriesNavCa(arg)) return true;
            return t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .Any(f => (f.FieldType.Assembly.GetName().Name ?? "")
                    .StartsWith("Microsoft.Dynamics.Nav", System.StringComparison.Ordinal));
        }

        static System.Collections.Generic.IEnumerable<System.Type> Flatten(System.Type t)
        {
            yield return t;
            if (!t.IsGenericType) yield break;
            foreach (var a in t.GetGenericArguments())
                foreach (var inner in Flatten(a)) yield return inner;
        }

        var offenders = program!
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
            .SelectMany(m => m.GetParameters().Select(p => p.ParameterType).Append(m.ReturnType)
                .SelectMany(Flatten)
                .Where(CarriesNavCa)
                .Select(bad => $"{m.Name} -> {bad.FullName}"))
            .Distinct()
            .ToList();

        Assert.True(offenders.Count == 0,
            "A value type carrying a Microsoft.Dynamics.Nav field reached a Program member signature. " +
            "That forces BC's CodeAnalysis assembly to load while arguments are being handled, which " +
            "crashes every cold-artifact-cache path. Project it to a plain shape (see AffectedObjectId). " +
            "Offenders: " + string.Join("; ", offenders));
    }
}
