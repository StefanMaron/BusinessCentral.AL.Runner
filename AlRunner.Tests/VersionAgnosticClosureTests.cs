// VersionAgnosticClosureTests — one engine binary spans a BC major's minors.
//
// Root cause being guarded
// ------------------------
// BC 28 modernised its runtime onto a large external closure — the Azure SDK (Azure.*)
// and the Microsoft.Identity / Microsoft.IdentityModel / System.IdentityModel token
// stack. Those assemblies are minor-version-pinned: a 28.2 R2R Base App binds
// Microsoft.Identity.ServiceEssentials.Core 1.43.0.0, whereas 28.1 shipped 1.39.0.0.
//
// RAR CopyLocal's the transitive closure of the 5 BC references into the app base dir
// at whatever _BCVersion the engine was built against. If those copies sit beside
// al-runner.dll, the default ALC binds them FIRST by simple name; a different-minor
// R2R app then requesting a higher version fails with FileLoadException 0x80131621 and
// never loads. That pinned one engine binary to exactly one BC minor.
//
// Fix (Directory.Build.targets target StripBcAppClosureFromCopyLocal): keep this closure OUT
// of bin — in EVERY project, since a referencing project runs its own ResolveAssemblyReferences
// and would re-copy the closure into its own bin. The DependencyLoader ALC Resolving handler
// serves any such assembly from the
// SELECTED version's artifact dir (BcArtifacts.ServiceTierDir) on demand, so the runner
// serves whichever minor the target project needs — from BC's own shipped copy.
//
// This test fails loudly if anyone re-introduces the CopyLocal (removes/breaks the target),
// which would silently re-pin the engine to one minor and break cross-minor projects.

using System.Reflection;
using Xunit;

namespace AlRunner.Tests;

public sealed class VersionAgnosticClosureTests
{
    // Representative members of the BC-app-only closure that MUST NOT be pinned in bin.
    // (Prefix-covered by the csproj strip: Microsoft.Identity*, System.IdentityModel*, Azure.*.)
    private static readonly string[] MustNotBeCopyLocal =
    {
        "Microsoft.Identity.ServiceEssentials.Core.dll", // the exact DLL whose 1.39-vs-1.43 skew broke 28.2
        "Microsoft.IdentityModel.Tokens.dll",
        "System.IdentityModel.Tokens.Jwt.dll",
        "Azure.Core.dll",
        // The one engine reference Microsoft stamps with a full 4-part assembly version
        // PER BC BUILD (17.0.36.40629 in 28.1.49838.50794, 17.0.40.3339 in 28.3.52162.53506)
        // instead of the MAJOR.0.0.0 contract the other engine DLLs honour. A bin copy
        // occupies the default ALC's slot for the simple name, so a different-build BC
        // requesting ITS CodeAnalysis version dies with FileLoadException 0x80131621 —
        // pinning the binary to the exact build it was compiled against.
        "Microsoft.Dynamics.Nav.CodeAnalysis.dll",
    };

    [Fact]
    public void BcAppClosure_IsNotCopiedIntoAppBaseDir()
    {
        var baseDir = AppContext.BaseDirectory;
        var leaked = MustNotBeCopyLocal
            .Where(dll => File.Exists(Path.Combine(baseDir, dll)))
            .ToArray();

        Assert.True(
            leaked.Length == 0,
            $"BC-app closure DLLs are CopyLocal'd into '{baseDir}': [{string.Join(", ", leaked)}]. " +
            "This re-pins the engine to a single BC minor and breaks cross-minor projects. " +
            "The Directory.Build.targets 'StripBcAppClosureFromCopyLocal' target must remove them " +
            "so the DependencyLoader ALC resolver serves the selected version from the artifact dir.");
    }

    [SkippableFact]
    public void Resolver_ServesBcAppClosure_FromSelectedArtifactDir_WhenArtifactsPresent()
    {
        // Env-guarded: only assert live resolution when an artifact dir is actually present.
        // An unprovisioned environment SKIPS visibly — it used to return, which xUnit records
        // as a pass, so "the resolver serves BC's copy" looked proven when nothing had run.
        string serviceTierDir = string.Empty;
        string? selectionError = null;
        try { serviceTierDir = AlRunner.Infrastructure.BcArtifacts.ServiceTierDir; }
        catch (Exception ex) { selectionError = ex.Message; }
        TestArtifacts.SkipIf(selectionError != null,
            $"no BC service-tier artifact directory could be selected: {selectionError}");

        var probe = Path.Combine(serviceTierDir, "Microsoft.Identity.ServiceEssentials.Core.dll");
        TestArtifacts.SkipIf(!File.Exists(probe),
            $"BC artifacts are incomplete: '{probe}' does not exist.");

        DependencyLoader.EnsureResolverInstalled_Public();

        var asm = Assembly.Load(new AssemblyName("Microsoft.Identity.ServiceEssentials.Core"));
        Assert.NotNull(asm);
        // It must come from the artifact dir (BC's shipped copy), not a bin copy.
        Assert.Equal(
            Path.GetFullPath(probe),
            Path.GetFullPath(asm.Location));
    }
}
