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

    /// <summary>The loops visible through <paramref name="locator"/>, as "file : type" (the walk of FindForwarderLoopMembers, reported per start).</summary>
    private static List<string> ForwarderLoops(IAssemblyLocator locator, IEnumerable<string> dirs)
    {
        var modules = new Dictionary<string, ModuleDefinition?>(StringComparer.Ordinal);
        ModuleDefinition? Read(string path)
        {
            if (modules.TryGetValue(path, out var m)) return m;
            try { m = ModuleDefinition.ReadModule(path, new ReaderParameters { ReadingMode = ReadingMode.Deferred }); }
            catch (BadImageFormatException) { m = null; }
            return modules[path] = m;
        }

        var loops = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var file in dirs.Where(Directory.Exists).SelectMany(d => Directory.EnumerateFiles(d, "*.dll")))
        {
            var start = Read(file);
            if (start == null) continue;
            foreach (var exported in start.ExportedTypes)
            {
                var current = exported;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                while (current.Scope is AssemblyNameReference scope)
                {
                    var path = locator.GetPathToAssembly(scope.FullName);
                    if (path == null) break;
                    if (!seen.Add(path + "|" + current.FullName))
                    {
                        loops.Add(Path.GetFileName(file) + " : " + current.FullName);
                        break;
                    }
                    var module = Read(path);
                    if (module == null || module.GetType(current.FullName) != null) break;
                    var next = module.ExportedTypes.FirstOrDefault(e => e.FullName == current.FullName);
                    if (next == null) break;
                    current = next;
                }
            }
        }
        return loops.ToList();
    }

    // Measured on tier 28.5.54151.55132 against runtime 8.0.30. A tier that needs another entry has
    // grown a new looping shim: confirm it IS a loop (the control below) before adding it.
    private static readonly string[] KnownLoopFiles =
    {
        "Microsoft.Win32.Registry.dll", "System.ComponentModel.Annotations.dll",
        "System.Numerics.Vectors.dll", "System.Security.AccessControl.dll",
        "System.Security.Principal.Windows.dll",
    };

    // Real implementations that carry a single forwarder to System.Runtime: hiding them turned a
    // working JsonDocument / Activity alias into AL0185 (review of #5442).
    private static readonly string[] RealImplementationsThatMustStayVisible =
        { "System.Text.Json.dll", "System.Diagnostics.DiagnosticSource.dll" };

    [SkippableFact]
    public void WithNoReferencePacks_TheRealServiceTierAndRuntimeNoLongerFormAForwarderLoop()
    {
        TestArtifacts.SkipIfMissing();
        var tier = BcCompiler.DefaultServiceTierDir;
        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        // The probing set of a runtime-only install: the tier, then the shared framework, no packs.
        var dirs = new[] { tier, runtime };

        // CONTROL: BC's own locator over exactly these directories. If this finds nothing there is
        // no loop on this tier + runtime and the assertion below could not have failed.
        var raw = new AssemblyLocator(dirs);
        var rawLoops = ForwarderLoops(raw, dirs);
        Skip.If(rawLoops.Count == 0,
            "this service tier and runtime form no forwarder loop without the reference packs, so " +
            "hiding nothing is correct here and this test cannot discriminate");

        var hidden = BcCompiler.FindForwarderLoopMembers(raw, dirs, runtime);
        var hiding = new BcCompiler.ForwarderShimHidingAssemblyLocator(new AssemblyLocator(dirs), hidden);
        var hiddenNames = hidden.Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Empty(ForwarderLoops(hiding, dirs));
        // The hiding removed files; it did not make the walk blind.
        Assert.NotEmpty(hidden);
        // Only loop members, and no real implementation among them.
        foreach (var name in RealImplementationsThatMustStayVisible)
            Assert.DoesNotContain(name, hiddenNames);
        Assert.Empty(hiddenNames.Except(KnownLoopFiles, StringComparer.Ordinal));
        // Everything hidden is a file that sits in the tier, never one from the runtime directory.
        Assert.All(hidden, f => Assert.StartsWith(Path.GetFullPath(tier), f, StringComparison.Ordinal));
    }
}
