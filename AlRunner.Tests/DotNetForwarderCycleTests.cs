// DotNetForwarderCycleTests — #5232.
//
// RUNNER-MECHANISM test. Nothing here is a statement about what Business Central does: the claim is
// about which assemblies the RUNNER hands BC's DotNet binder when the .NET reference packs are
// missing. The corpus runs on a service tier with no notion of an SDK `packs/` directory, so there
// is no corpus half.
//
// CLAIM: on a runtime-only dotnet install the service tier's netstandard-era copies of a few System
// assemblies forward types to `mscorlib`, and the runtime's `mscorlib` forwards them back to the
// tier copy (the only one whose public key token matches), so BC's Cecil follows the loop until the
// stack is gone (exit 134). The runner hides exactly those copies in that state.
//
// What the whole-pipeline run showed (not repeatable here, because the test host's own dotnet root
// has packs): a `DotNet` alias of System.Diagnostics.Eventing.Reader.EventRecord compiled against a
// copy of the .NET 8 runtime with no packs/ aborted before the change and passed after it.
//
// TRAP: the chase below mirrors CecilAssemblyResolver.Resolve (locator.GetPathToAssembly(FullName),
// then read that file); the CONTROL (the raw locator over the same directories finds loops) is what
// keeps a zero from a walk that cannot see them from reading as a pass.

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
    // Which files are hidden: judged from the files, on synthetic assemblies.
    // ---------------------------------------------------------------------------------------

    private static void WriteAssembly(string dir, string name, string? forwardsTypeToAssembly)
    {
        Directory.CreateDirectory(dir);
        var asm = AssemblyDefinition.CreateAssembly(
            new AssemblyNameDefinition(name, new Version(1, 0, 0, 0)), name, ModuleKind.Dll);
        if (forwardsTypeToAssembly != null)
            asm.MainModule.ExportedTypes.Add(new ExportedType("Some.Ns", "Forwarded", asm.MainModule,
                new AssemblyNameReference(forwardsTypeToAssembly, new Version(4, 0, 0, 0))));
        asm.Write(Path.Combine(dir, name + ".dll"));
    }

    [Fact]
    public void OnlyATierFileThatForwardsTypesAndShadowsARuntimeAssemblyIsHidden()
    {
        var tier = Path.Combine(_root, "tier");
        var runtime = Path.Combine(_root, "runtime");
        WriteAssembly(tier, "Shim", forwardsTypeToAssembly: "mscorlib");          // forwards + in runtime: HIDDEN
        WriteAssembly(runtime, "Shim", forwardsTypeToAssembly: null);
        WriteAssembly(tier, "RealImpl", forwardsTypeToAssembly: null);            // same name, no forwarders: kept
        WriteAssembly(runtime, "RealImpl", forwardsTypeToAssembly: null);
        WriteAssembly(tier, "TierOnlyForwarder", forwardsTypeToAssembly: "mscorlib"); // forwards, no runtime twin: kept
        WriteAssembly(runtime, "RuntimeOnly", forwardsTypeToAssembly: "mscorlib");  // the runtime dir itself is never hidden

        var hidden = BcCompiler.FindForwarderShimsShadowingTheRuntime(new[] { tier, runtime }, runtime);

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

    private static BcCompiler.DotNetRefPackGap Gap(bool aliasesCannotBind)
        => new("warning", aliasesCannotBind, "cause");

    /// <summary>
    /// The wiring: the hiding applies exactly when the packs are missing. With packs present (null),
    /// or present for another major (AliasesCannotBind false), BC's own locator is used untouched.
    /// </summary>
    [Fact]
    public void TheHidingIsWiredInOnlyWhenTheReferencePacksAreMissing()
    {
        var tier = Path.Combine(_root, "tier");
        var runtime = Path.Combine(_root, "runtime");
        WriteAssembly(tier, "Shim", forwardsTypeToAssembly: "mscorlib");
        WriteAssembly(runtime, "Shim", forwardsTypeToAssembly: null);
        var shim = Path.GetFullPath(Path.Combine(tier, "Shim.dll"));
        var inner = new FixedLocator(new() { ["Shim"] = shim });
        var dirs = new[] { tier, runtime };

        var missing = BcCompiler.GuardAgainstForwarderLoops(inner, dirs, runtime, Gap(aliasesCannotBind: true));
        Assert.Null(missing.GetPathToAssembly("Shim"));

        Assert.Same(inner, BcCompiler.GuardAgainstForwarderLoops(inner, dirs, runtime, gap: null));
        Assert.Same(inner, BcCompiler.GuardAgainstForwarderLoops(inner, dirs, runtime, Gap(aliasesCannotBind: false)));
        Assert.Equal(shim, inner.GetPathToAssembly("Shim"));   // control: the shim is findable when not hidden
    }

    // ---------------------------------------------------------------------------------------
    // The real service tier and the real runtime: does BC's own locator send Cecil round a loop?
    // ---------------------------------------------------------------------------------------

    /// <summary>
    /// Forwarder chains that close on themselves, walked the way CecilAssemblyResolver walks them:
    /// ask the locator for the scope's full name, read that file, look for the type there, follow the
    /// next ExportedType. A repeat of (file, type) is a loop. Starts from every exported type of every
    /// assembly in <paramref name="dirs"/>.
    /// </summary>
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

        var hidden = BcCompiler.FindForwarderShimsShadowingTheRuntime(dirs, runtime);
        var hiding = new BcCompiler.ForwarderShimHidingAssemblyLocator(new AssemblyLocator(dirs), hidden);

        Assert.Empty(ForwarderLoops(hiding, dirs));
        // The hiding is the whole story: it removed files, it did not make the walk blind.
        Assert.NotEmpty(hidden);
    }
}
