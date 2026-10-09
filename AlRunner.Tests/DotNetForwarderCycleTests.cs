// DotNetForwarderCycleTests — #5232.
//
// RUNNER-MECHANISM test. Nothing here is a statement about what Business Central does: the claim is
// about which assemblies the RUNNER hands BC's DotNet binder when the .NET reference packs are
// missing. The corpus runs on a service tier with no notion of an SDK `packs/` directory, so there
// is no corpus half.
//
// CLAIM: on a runtime-only dotnet install the service tier's netstandard-era copies of a few System
// assemblies forward types onward and the runtime's facades forward them back to the tier copy (the
// only one whose public key token matches), so BC's Cecil follows the loop until the stack is gone
// (exit 134). The runner hides exactly the files that sit on such a loop, and no others: the tier's
// real implementations that merely forward one type to System.Runtime (System.Text.Json,
// System.Diagnostics.DiagnosticSource) stay visible.
//
// What the whole-pipeline run showed (not repeatable here, because the test host's own dotnet root
// has packs): a `DotNet` alias of System.Diagnostics.Eventing.Reader.EventRecord compiled against a
// copy of the .NET 8 runtime with no packs/ aborted before the change and passed after it.
//
// TRAP: the chase mirrors CecilAssemblyResolver.Resolve (locator.GetPathToAssembly(FullName), then
// read that file); the CONTROL (the raw locator over the same directories finds loops) is what keeps
// a zero from a walk that cannot see them from reading as a pass.

using Microsoft.Dynamics.Nav.CodeAnalysis.DotNet;
using Mono.Cecil;
using Xunit;

namespace AlRunner.Tests;

public sealed class DotNetForwarderCycleTests : IDisposable
{
    private readonly string _root = TestScratch.Dir("al-runner-5232-forwarders");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------------------------------
    // Which files are hidden: judged from the loops, on synthetic assemblies.
    // ---------------------------------------------------------------------------------------

    private const string Fwd = "Some.Ns.Forwarded";

    /// <summary>An assembly that forwards <see cref="Fwd"/> to <paramref name="forwardsTo"/>, or defines it.</summary>
    private static void WriteAssembly(string dir, string name, string? forwardsTo, bool defines = false)
    {
        Directory.CreateDirectory(dir);
        var asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(name, new Version(1, 0, 0, 0)), name, ModuleKind.Dll);
        if (forwardsTo != null)
        {
            // The reference must be in AssemblyReferences, or Cecil writes the forwarder with no scope.
            var target = new AssemblyNameReference(forwardsTo, new Version(1, 0, 0, 0));
            asm.MainModule.AssemblyReferences.Add(target);
            asm.MainModule.ExportedTypes.Add(new ExportedType("Some.Ns", "Forwarded", asm.MainModule, target));
        }
        if (defines)
            asm.MainModule.Types.Add(new TypeDefinition("Some.Ns", "Forwarded",
                Mono.Cecil.TypeAttributes.Public | Mono.Cecil.TypeAttributes.Class, asm.MainModule.TypeSystem.Object));
        asm.Write(Path.Combine(dir, name + ".dll"));
    }

    /// <summary>Resolves a reference by simple name to the first probing directory holding it.</summary>
    private sealed class FirstFoundLocator(IEnumerable<string> dirs) : IAssemblyLocator
    {
        public IEnumerable<string> ProbingPaths => dirs;
        public string? GetPathToAssembly(string assemblyName)
        {
            var simple = assemblyName.Split(',')[0];
            return dirs.Select(d => Path.Combine(d, simple + ".dll")).FirstOrDefault(File.Exists);
        }
    }

    [Fact]
    public void OnlyTheTierFilesOnAForwarderLoopAreSelected()
    {
        var tier = Path.Combine(_root, "tier");
        var runtime = Path.Combine(_root, "runtime");
        // A loop: tier Shim -> runtime Facade -> tier Shim (the facade finds the tier file by name).
        WriteAssembly(tier, "Shim", forwardsTo: "Facade");
        WriteAssembly(runtime, "Facade", forwardsTo: "Shim");
        // NOT a loop: a tier real implementation that also forwards one type, to an assembly that defines it.
        WriteAssembly(tier, "RealImplWithOneForwarder", forwardsTo: "Core");
        WriteAssembly(runtime, "Core", forwardsTo: null, defines: true);
        // NOT a loop: a forwarder whose target is nowhere.
        WriteAssembly(tier, "ForwardsIntoTheVoid", forwardsTo: "Nowhere");
        // NOT a loop: a tier file with no forwarders at all.
        WriteAssembly(tier, "Plain", forwardsTo: null, defines: true);

        var dirs = new[] { tier, runtime };
        var hidden = BcCompiler.FindForwarderLoopMembers(new FirstFoundLocator(dirs), dirs, runtime);

        // Control: the walk does see the loop from the runtime side too, and the runtime facade is
        // part of it, yet only the tier file is ever offered for hiding.
        Assert.Equal(new[] { Path.GetFullPath(Path.Combine(tier, "Shim.dll")) }, hidden.ToArray());
    }

    private sealed class FixedLocator(Dictionary<string, string?> paths) : IAssemblyLocator
    {
        public IEnumerable<string> ProbingPaths => Array.Empty<string>();
        public string? GetPathToAssembly(string assemblyName)
            => paths.TryGetValue(assemblyName, out var p) ? p : null;
    }

    [Fact]
    public void TheHidingLocatorAnswersNotFoundForAHiddenFile_AndPassesEverythingElseThrough()
    {
        var hiddenPath = Path.GetFullPath(Path.Combine(_root, "tier", "Shim.dll"));
        var keptPath = Path.GetFullPath(Path.Combine(_root, "tier", "Other.dll"));
        var inner = new FixedLocator(new() { ["Shim"] = hiddenPath, ["Other"] = keptPath, ["Missing"] = null });
        var locator = new BcCompiler.ForwarderShimHidingAssemblyLocator(inner, new() { hiddenPath });

        Assert.Null(locator.GetPathToAssembly("Shim"));
        Assert.Equal(keptPath, locator.GetPathToAssembly("Other"));
        Assert.Null(locator.GetPathToAssembly("Missing"));
    }

    // ---------------------------------------------------------------------------------------
    // The wiring: the call chain GetOrCreateDotNetFactory runs, driven with a forced install shape.
    // ---------------------------------------------------------------------------------------

    private string DotnetRoot(string name, bool withPacks)
    {
        var root = Path.Combine(_root, name);
        var runtimeDir = Path.Combine(root, "shared", "Microsoft.NETCore.App", "8.0.30");
        Directory.CreateDirectory(runtimeDir);
        if (withPacks)
        {
            Directory.CreateDirectory(Path.Combine(root, "packs", "Microsoft.NETCore.App.Ref", "8.0.30", "ref", "net8.0"));
            Directory.CreateDirectory(Path.Combine(root, "packs", "NETStandard.Library.Ref", "2.1.0", "ref", "netstandard2.1"));
        }
        return runtimeDir + Path.DirectorySeparatorChar;
    }

    [Fact]
    public void TheFactoryIsGivenTheHidingLocatorOnlyWhenTheReferencePacksAreMissing()
    {
        var tier = Path.Combine(_root, "tier");
        WriteAssembly(tier, "Shim", forwardsTo: "Facade");

        var packless = DotnetRoot("packless", withPacks: false);
        WriteAssembly(packless, "Facade", forwardsTo: "Shim");
        var factory = BcCompiler.CreateDotNetResolverFactory(new[] { tier, packless }, packless, null, out var missingLocator);
        Assert.NotNull(factory);
        Assert.IsType<BcCompiler.ForwarderShimHidingAssemblyLocator>(missingLocator);

        var withPacks = DotnetRoot("withpacks", withPacks: true);
        BcCompiler.CreateDotNetResolverFactory(new[] { tier, withPacks }, withPacks, null, out var presentLocator);
        Assert.IsType<AssemblyLocator>(presentLocator);   // BC's own locator, untouched
    }

    // ---------------------------------------------------------------------------------------
    // The real service tier and the real runtime: does BC's own locator send Cecil round a loop?
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// The files on forwarder loops visible through <paramref name="locator"/>: an independent
    /// re-statement of FindForwarderLoopMembers' walk (ask the locator for the scope's full name,
    /// read that file, look for the type there, follow the next ExportedType; a repeat of
    /// (file, type) closes a loop and the stretch since its first visit is the loop). Independent on
    /// purpose, so asserting the product's selection equals it is a check and not a tautology.
    /// </summary>
    private static SortedSet<string> LoopMembers(IAssemblyLocator locator, IEnumerable<string> dirs)
    {
        var modules = new Dictionary<string, ModuleDefinition?>(StringComparer.Ordinal);
        ModuleDefinition? Read(string path)
        {
            if (modules.TryGetValue(path, out var m)) return m;
            try { m = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadingMode = ReadingMode.Deferred }); }
            catch (BadImageFormatException) { m = null; }
            return modules[path] = m;
        }

        var members = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in dirs.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.dll")))
        {
            var start = Read(file);
            if (start == null) continue;
            foreach (var exported in start.ExportedTypes)
            {
                var current = exported;
                var visited = new List<string>();
                var firstVisit = new Dictionary<string, int>(StringComparer.Ordinal);
                while (current.Scope is AssemblyNameReference scope)
                {
                    var path = locator.GetPathToAssembly(scope.FullName);
                    if (path == null) break;
                    path = Path.GetFullPath(path);
                    var key = path + "|" + current.FullName;
                    if (firstVisit.TryGetValue(key, out var at))
                    {
                        foreach (var m in visited.Skip(at)) members.Add(m);
                        break;
                    }
                    firstVisit[key] = visited.Count;
                    visited.Add(path);
                    var module = Read(path);
                    if (module == null || module.GetType(current.FullName) != null) break;
                    var next = module.ExportedTypes.FirstOrDefault(e => e.FullName == current.FullName);
                    if (next == null) break;
                    current = next;
                }
            }
        }
        return members;
    }

    // Measured expectations, by the BC MAJOR of the service tier under test (#5448). The loop set is a
    // property of the tier's netstandard-era copies, not of the runtime the test host runs on: a 28.5
    // tier hides the same five files on runtimes 8, 9 and 10. A tier major not listed has no
    // exact-set assertion and that test skips by name; the property test above still gates it.
    // BC 29 was measured on runtime 10 only, the one runtime a net10.0 build (BC 29+) runs on.
    private static readonly string[] LoopFilesOfBc27And28 =
    {
        "Microsoft.Win32.Registry.dll", "System.ComponentModel.Annotations.dll",
        "System.Numerics.Vectors.dll", "System.Security.AccessControl.dll",
        "System.Security.Principal.Windows.dll",
    };

    private static readonly Dictionary<int, string[]> ExpectedLoopFilesByTierMajor = new()
    {
        [27] = LoopFilesOfBc27And28,
        [28] = LoopFilesOfBc27And28,
        // BC 29.0.54011.55935 tier against runtime 10.0.11: the five above are not loops there.
        [29] = new[] { "System.Diagnostics.EventLog.dll" },
    };

    /// <summary>
    /// The BC major of the tier directory, read from its own Ncl.dll file version. Never defaulted:
    /// this runs only after the artifacts gate passed, so an absent or unreadable Ncl.dll is a broken
    /// measurement and fails loudly instead of selecting no row (which would skip).
    /// </summary>
    private static int TierMajor(string tierDir)
    {
        var ncl = Path.Combine(tierDir, "Microsoft.Dynamics.Nav.Ncl.dll");
        Assert.True(File.Exists(ncl), $"cannot establish the tier's BC major: {ncl} is missing");
        var major = System.Diagnostics.FileVersionInfo.GetVersionInfo(ncl).FileMajorPart;
        Assert.True(major >= 27, $"cannot establish the tier's BC major: {ncl} reads file major {major}, not a BC 27+ version");
        return major;
    }

    // Real implementations that carry a single forwarder to System.Runtime: hiding them turned a
    // working JsonDocument / Activity alias into AL0185 (review of #5442).
    private static readonly string[] RealImplementationsThatMustStayVisible =
        { "System.Text.Json.dll", "System.Diagnostics.DiagnosticSource.dll" };

    private static (string[] Dirs, string Tier, string Runtime) RealProbingSet()
    {
        var tier = BcCompiler.DefaultServiceTierDir;
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        // The probing set of a runtime-only install: the tier, then the shared framework, no packs.
        return (new[] { tier, runtime }, tier, runtime);
    }

    /// <summary>
    /// PROPERTIES that hold on any tier and runtime: the selection is exactly the tier-side members of
    /// the loops the raw locator sends the walk round, nothing remains looping once they are hidden,
    /// the real implementations stay visible, and nothing outside the tier directory is hidden.
    /// </summary>
    [SkippableFact]
    public void WithNoReferencePacks_TheSelectionIsExactlyTheTierFilesOnLoops_AndNoLoopRemains()
    {
        TestArtifacts.SkipIfMissing();
        var (dirs, tier, runtime) = RealProbingSet();

        // CONTROL: BC's own locator over exactly these directories. If this finds nothing there is
        // no loop on this tier + runtime and the assertions below could not have failed.
        var raw = new AssemblyLocator(dirs);
        var rawMembers = LoopMembers(raw, dirs);
        Skip.If(rawMembers.Count == 0,
            "this service tier and runtime form no forwarder loop without the reference packs, so " +
            "hiding nothing is correct here and this test cannot discriminate");

        var hidden = BcCompiler.FindForwarderLoopMembers(raw, dirs, runtime);
        var runtimeFull = Path.GetFullPath(runtime).TrimEnd(Path.DirectorySeparatorChar);
        var expected = rawMembers
            .Where(f => !Path.GetDirectoryName(f)!.TrimEnd(Path.DirectorySeparatorChar)
                .Equals(runtimeFull, StringComparison.Ordinal))
            .ToArray();

        // (i) every hidden file is a loop member, and every tier-side loop member is hidden
        Assert.Equal(expected, hidden.OrderBy(f => f, StringComparer.Ordinal).ToArray());
        Assert.NotEmpty(hidden);
        // (ii) after hiding, the same walk finds no loop
        var hiding = new BcCompiler.ForwarderShimHidingAssemblyLocator(new AssemblyLocator(dirs), hidden);
        Assert.Empty(LoopMembers(hiding, dirs));
        // (iii) the real implementations stay visible
        var hiddenNames = hidden.Select(Path.GetFileName).ToArray();
        foreach (var name in RealImplementationsThatMustStayVisible)
            Assert.DoesNotContain(name, hiddenNames);
        // (iv) only tier files
        Assert.All(hidden, f => Assert.StartsWith(Path.GetFullPath(tier), f, StringComparison.Ordinal));
    }

    /// <summary>
    /// The EXACT set, for the tier majors it was measured on. A tier that grows a loop fails here
    /// naming the new member, which forces a conscious update (and a look at what aliases lose);
    /// a tier major with no measurement skips by name rather than passing.
    /// </summary>
    [SkippableFact]
    public void WithNoReferencePacks_TheHiddenSetIsTheMeasuredOneForThisTier()
    {
        TestArtifacts.SkipIfMissing();
        var (dirs, tier, runtime) = RealProbingSet();
        var major = TierMajor(tier);
        Skip.IfNot(ExpectedLoopFilesByTierMajor.TryGetValue(major, out var known),
            $"no measured hidden set for a BC {major} tier; the property test gates this tier");

        var hidden = BcCompiler.FindForwarderLoopMembers(new AssemblyLocator(dirs), dirs, runtime)
            .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        var added = hidden.Except(known!, StringComparer.Ordinal).ToArray();
        var gone = known!.Except(hidden, StringComparer.Ordinal).ToArray();
        Assert.True(added.Length == 0 && gone.Length == 0,
            $"hidden set changed on this tier (BC {major}, runtime .NET {Environment.Version.Major}): new loop members [{string.Join(", ", added)}], " +
            $"no longer looping [{string.Join(", ", gone)}]. Confirm each new member is a real loop " +
            "(the property test proves it) and update ExpectedLoopFilesByTierMajor.");
    }
}
