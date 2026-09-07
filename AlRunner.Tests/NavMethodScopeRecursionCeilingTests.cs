// NavMethodScopeRecursionCeilingTests — issue #3405.
//
// BC's NavMethodScope..ctor refuses once StackDepth exceeds the private const
// MaxStackDepth on that same type. The runner's guard was a hardcoded 500, so an AL call
// chain 501..1000 frames deep ran on a real service tier and was refused here — a FALSE
// FAILURE, which makes correct AL look broken. The ceiling is now read out of the loaded
// Ncl instead of being chosen.
//
// The BC-behaviour half of this claim ("BC allows up to 1000 and errors past it") is
// asserted upstream in the corpus, where a real service tier adjudicates it. What is
// proven here is only what is about the RUNNER:
//
//   - the resolver reads the const out of a real assembly (const has no storage, so this
//     is GetRawConstantValue, not GetValue — a detail the fix depends on and that a test
//     asserting only the number would not pin);
//   - the fallback engages, rather than throwing, for every shape that could go wrong;
//   - MaxRecursionDepthResolvedFromNcl distinguishes "read it" from "fell back", so a
//     silent permanent fallback cannot masquerade as a resolved value. A fallback nobody
//     noticed is exactly how the stale 500 survived;
//   - DIVERGENCE GUARD: every provisioned BC build's real MaxStackDepth still equals the
//     fallback. This is the test that stops #3405 recurring — if Microsoft moves the
//     constant, this fails and names the build, instead of the runner quietly refusing
//     at the wrong depth again.
using AlRunner.Tests;
using Mono.Cecil;
using Xunit;

namespace AlRunner.Tests;

public sealed class NavMethodScopeRecursionCeilingTests
{
    /// <summary>The value the fix falls back to, and the value every supported BC build
    /// currently declares. Kept as a literal so the divergence guard below compares BC's
    /// number against a number written down here, not against itself.</summary>
    private const int ExpectedBcMaxStackDepth = 1000;

    // ── Stand-in shapes for the resolver ────────────────────────────────────────────────
    // Each models one way the loaded Ncl could disagree with what the resolver expects.
    // They are private nested types so they cannot be mistaken for anything BC ships.

    private static class ShapeOk { private const int MaxStackDepth = 1000; }

    private static class ShapeDifferentValue { private const int MaxStackDepth = 2500; }

    private static class ShapeAbsent { private const int SomethingElse = 1000; }

    // Not a const: a static readonly field has storage and no metadata constant, so
    // GetRawConstantValue would throw on it. The resolver must reject it, not fall over.
    private static class ShapeNotLiteral { internal static readonly int MaxStackDepth = 1000; }

    // Right name, right const-ness, wrong type.
    private static class ShapeWrongType { private const string MaxStackDepth = "1000"; }

    // A value that would disable the guard entirely rather than move it.
    private static class ShapeNonPositive { private const int MaxStackDepth = 0; }

    private static void Restore() =>
        // Leave the process in the state the rest of the suite expects: nothing loaded,
        // so the fallback is in force. These tests mutate a static, so they must not leak.
        BcRuntime.ResolveMaxRecursionDepth(null);

    [Fact]
    public void Resolve_ReadsTheConstFromTheType_AndReportsItAsResolved()
    {
        try
        {
            BcRuntime.ResolveMaxRecursionDepth(typeof(ShapeOk));

            Assert.Equal(1000, BcRuntime.MaxRecursionDepth);
            Assert.True(BcRuntime.MaxRecursionDepthResolvedFromNcl);
        }
        finally { Restore(); }
    }

    [Fact]
    public void Resolve_AdoptsAValueThatIsNotTheFallback()
    {
        // The point of reading the constant is that a DIFFERENT number is adopted. If this
        // passed only for 1000, the resolver would be indistinguishable from the hardcoded
        // constant it replaces — which is the defect, not the fix.
        try
        {
            BcRuntime.ResolveMaxRecursionDepth(typeof(ShapeDifferentValue));

            Assert.Equal(2500, BcRuntime.MaxRecursionDepth);
            Assert.True(BcRuntime.MaxRecursionDepthResolvedFromNcl);
        }
        finally { Restore(); }
    }

    [Theory]
    [InlineData(typeof(ShapeAbsent))]
    [InlineData(typeof(ShapeNotLiteral))]
    [InlineData(typeof(ShapeWrongType))]
    [InlineData(typeof(ShapeNonPositive))]
    public void Resolve_FallsBackWithoutThrowing_WhenTheShapeIsNotWhatWeExpect(Type shape)
    {
        try
        {
            BcRuntime.ResolveMaxRecursionDepth(shape);

            // Concrete value, not merely "did not throw": the guard must still refuse
            // somewhere, and it must refuse at BC's documented ceiling.
            Assert.Equal(ExpectedBcMaxStackDepth, BcRuntime.MaxRecursionDepth);
            // ...and the run must be able to tell it fell back. This is the assertion that
            // makes a silent permanent fallback visible.
            Assert.False(BcRuntime.MaxRecursionDepthResolvedFromNcl);
        }
        finally { Restore(); }
    }

    [Fact]
    public void Resolve_FallsBackOnANullType()
    {
        try
        {
            BcRuntime.ResolveMaxRecursionDepth(null);

            Assert.Equal(ExpectedBcMaxStackDepth, BcRuntime.MaxRecursionDepth);
            Assert.False(BcRuntime.MaxRecursionDepthResolvedFromNcl);
        }
        finally { Restore(); }
    }

    [Fact]
    public void TheCeilingIsNeverTheOldHardcoded500()
    {
        // The regression this issue is about, stated directly: 500 refused AL that real BC
        // runs. Whichever path produced the current value, it must not be that number.
        try
        {
            BcRuntime.ResolveMaxRecursionDepth(null);
            Assert.NotEqual(500, BcRuntime.MaxRecursionDepth);

            BcRuntime.ResolveMaxRecursionDepth(typeof(ShapeOk));
            Assert.NotEqual(500, BcRuntime.MaxRecursionDepth);
        }
        finally { Restore(); }
    }

    // ── Divergence guard against the real, provisioned BC builds ────────────────────────

    /// <summary>
    /// Every provisioned BC build's real <c>NavMethodScope.MaxStackDepth</c> must equal the
    /// fallback. Read with Cecil rather than reflection so this needs no BC engine bootstrap
    /// and can check EVERY provisioned version in one test, not just the one loaded.
    ///
    /// This is the test that stops #3405 recurring: the previous 500 was wrong and nothing
    /// noticed, because nothing compared it to BC.
    /// </summary>
    [SkippableFact]
    public void EveryProvisionedBcBuildDeclaresTheFallbackAsItsMaxStackDepth()
    {
        TestArtifacts.SkipIfMissing();

        var home = TestArtifacts.HomeDir();
        Skip.If(string.IsNullOrEmpty(home), "no home directory");
        var root = TestArtifacts.StandardCacheDir(home!);
        TestArtifacts.SkipIfDirectoryMissing(root, "BC artifacts");

        var checkedBuilds = new List<string>();
        var divergent = new List<string>();

        foreach (var dir in Directory.EnumerateDirectories(root).OrderBy(d => d))
        {
            var ncl = Path.Combine(dir, "Microsoft.Dynamics.Nav.Ncl.dll");
            if (!File.Exists(ncl)) continue;

            using var asm = AssemblyDefinition.ReadAssembly(ncl);
            var t = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavMethodScope");
            if (t == null)
            {
                divergent.Add($"{Path.GetFileName(dir)}: NavMethodScope type absent");
                continue;
            }

            var f = t.Fields.FirstOrDefault(x => x.Name == "MaxStackDepth");
            if (f == null || !f.HasConstant || f.Constant is not int value)
            {
                divergent.Add($"{Path.GetFileName(dir)}: MaxStackDepth absent or not an int const");
                continue;
            }

            checkedBuilds.Add(Path.GetFileName(dir));
            if (value != ExpectedBcMaxStackDepth)
                divergent.Add($"{Path.GetFileName(dir)}: MaxStackDepth={value}");
        }

        // A guard that silently checked nothing would pass forever. Require that it actually
        // read at least one build before believing its verdict.
        Skip.If(checkedBuilds.Count == 0, "no provisioned build carries Microsoft.Dynamics.Nav.Ncl.dll");

        Assert.True(
            divergent.Count == 0,
            $"BC's NavMethodScope.MaxStackDepth no longer matches the runner's fallback of "
            + $"{ExpectedBcMaxStackDepth}. Update FallbackMaxRecursionDepth in "
            + $"AlRunner/Patches/MethodScopePatches.cs and docs/recursion-depth.md. Divergent: "
            + string.Join("; ", divergent));
    }

    /// <summary>
    /// BC's real field must have the shape the resolver's <c>BindingFlags</c> assume. The
    /// stand-in shapes above cannot prove this: they were written to match, so agreeing with
    /// them is circular. Non-public, static and literal are each a way the lookup could come
    /// back null against the real assembly while every stand-in test still passed.
    /// </summary>
    [SkippableFact]
    public void TheRealNclFieldHasTheShapeTheResolverLooksFor()
    {
        TestArtifacts.SkipIfMissing();

        var home = TestArtifacts.HomeDir();
        Skip.If(string.IsNullOrEmpty(home), "no home directory");
        var root = TestArtifacts.StandardCacheDir(home!);
        TestArtifacts.SkipIfDirectoryMissing(root, "BC artifacts");

        var ncl = Directory.EnumerateDirectories(root).OrderBy(d => d)
            .Select(d => Path.Combine(d, "Microsoft.Dynamics.Nav.Ncl.dll"))
            .FirstOrDefault(File.Exists);
        Skip.If(ncl == null, "no provisioned build carries Microsoft.Dynamics.Nav.Ncl.dll");

        using var asm = AssemblyDefinition.ReadAssembly(ncl!);
        var msType = asm.MainModule.GetType("Microsoft.Dynamics.Nav.Runtime.NavMethodScope");
        Skip.If(msType == null, "NavMethodScope not found in the provisioned Ncl");

        var f = msType!.Fields.FirstOrDefault(x => x.Name == "MaxStackDepth");
        Assert.NotNull(f);

        // Each of these is a BindingFlags assumption the fix makes.
        Assert.True(f!.IsStatic, "MaxStackDepth is expected to be static");
        Assert.True(f.IsLiteral, "MaxStackDepth is expected to be a const (GetRawConstantValue)");
        Assert.True(f.IsPrivate, "MaxStackDepth is expected to be private (BindingFlags.NonPublic)");
        Assert.Equal("System.Int32", f.FieldType.FullName);
        Assert.Equal(ExpectedBcMaxStackDepth, Assert.IsType<int>(f.Constant));
    }
}
