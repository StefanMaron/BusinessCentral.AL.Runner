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

    // The name list above is a sample; this is the property itself. A deps.json library of type
    // "reference" is one RAR copied from a HintPath closure — here, the BC service-tier dir — as
    // opposed to a NuGet "package" or a "project". Any such file sitting in bin shadows the
    // SELECTED artifact's copy, and two builds of one minor differ (Microsoft.Bcl.AsyncInterfaces
    // 10.0.0.2 in 28.1.49838.53910, 10.0.0.5 in 28.1.49838.54424): #3977, #4527.
    // Ncl.dll is the one deliberate exception, in this test project only (Directory.Build.targets).
    // The runner's own bin is checked too: it is what ships in the nupkg, and the strip rule
    // already conditions on the project name, so the two outputs can disagree.
    [Fact]
    public void NoServiceTierSourcedReference_IsCopiedIntoAppBaseDir()
    {
        var baseDir = AppContext.BaseDirectory;
        AssertNoServiceTierLeaks(baseDir, exemptNcl: true);

        // AlRunner.Tests/bin/<config>/<tfm>/ -> AlRunner/bin/<config>/<tfm>/
        var tfmDir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(baseDir));
        var configDir = tfmDir.Parent!;
        var repoRoot = configDir.Parent!.Parent!.Parent!.FullName;
        var runnerBin = Path.Combine(repoRoot, "AlRunner", "bin", configDir.Name, tfmDir.Name);
        Assert.True(File.Exists(Path.Combine(runnerBin, "al-runner.deps.json")),
            $"runner output '{runnerBin}' has no al-runner.deps.json — the runner's own bin was not measured");
        AssertNoServiceTierLeaks(runnerBin, exemptNcl: false);
    }

    private static void AssertNoServiceTierLeaks(string baseDir, bool exemptNcl)
    {
        var depsFiles = Directory.GetFiles(baseDir, "*.deps.json");
        Assert.NotEmpty(depsFiles); // no manifest means nothing below was measured

        // Across every manifest in bin: al-runner.deps.json travels with the project reference
        // and lists BC's Newtonsoft.Json as a reference, while the file there is the test SDK's.
        var runtimeFilesByType = new Dictionary<string, List<string>>();
        var referenceLibraries = 0;
        foreach (var depsFile in depsFiles)
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(depsFile));
            var libraries = doc.RootElement.GetProperty("libraries");
            foreach (var target in doc.RootElement.GetProperty("targets").EnumerateObject())
            foreach (var lib in target.Value.EnumerateObject())
            {
                if (!libraries.TryGetProperty(lib.Name, out var meta)) continue;
                var type = meta.GetProperty("type").GetString() ?? "";
                if (type == "reference") referenceLibraries++;
                if (!lib.Value.TryGetProperty("runtime", out var runtime)) continue;
                if (!runtimeFilesByType.TryGetValue(type, out var files))
                    runtimeFilesByType[type] = files = new List<string>();
                files.AddRange(runtime.EnumerateObject().Select(f => Path.GetFileName(f.Name)));
            }
        }

        // A file a NuGet package also provides is that package's copy (the test SDK's
        // Newtonsoft.Json, the runner's own System.Text.Json), not the service tier's.
        var packageFiles = runtimeFilesByType.GetValueOrDefault("package") ?? new List<string>();
        var leaked = new List<string>();
        foreach (var name in runtimeFilesByType.GetValueOrDefault("reference") ?? new List<string>())
        {
            // al-runner.dll is the project under test; the test manifest lists it as a reference.
            if (name == "al-runner.dll") continue;
            if (exemptNcl && name == "Microsoft.Dynamics.Nav.Ncl.dll") continue;
            if (packageFiles.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            if (File.Exists(Path.Combine(baseDir, name))) leaked.Add(name);
        }

        // The manifest must actually list the BC closure, or an empty `leaked` proves nothing.
        Assert.True(referenceLibraries > 0,
            $"no 'reference' libraries in {string.Join(", ", depsFiles)} — the BC closure was not measured");
        Assert.True(leaked.Count == 0,
            $"service-tier-sourced DLLs are CopyLocal'd into '{baseDir}': [{string.Join(", ", leaked.Distinct().OrderBy(n => n))}]. " +
            "Each pins the runner to the exact BC build it was compiled against. " +
            "Directory.Build.targets 'StripBcAppClosureFromCopyLocal' must remove every file RAR copied out of $(ServiceTierPath).");
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
