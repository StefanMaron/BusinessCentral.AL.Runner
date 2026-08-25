// EngineLoadContextTests — pins AlRunner.Infrastructure.EngineLoadContext, the
// consolidation of 8 previously-hardcoded AssemblyLoadContext.Default call sites (found
// during the #2027 canary — see that issue's comment trail). No-op today (al-runner.dll
// IS the process's root/Default app), but the type must actually resolve "whichever ALC
// loaded THIS assembly," not just always return Default — a gutted implementation that
// hardcoded `=> AssemblyLoadContext.Default` would pass a same-context assertion just as
// well, so the proving test is LoadFromBytes landing a freshly-compiled assembly in
// EXACTLY the ALC Current reports, checked via AssemblyLoadContext.GetLoadContext on the
// loaded result — not merely "it didn't throw".
using System.Reflection;
using System.Runtime.Loader;
using AlRunner.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

public sealed class EngineLoadContextTests
{
    private static byte[] CompileTrivialAssembly(string assemblyName)
    {
        var tree = CSharpSyntaxTree.ParseText($"public class {assemblyName}_Marker {{ }}");
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { tree },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(d => d.ToString())));
        return ms.ToArray();
    }

    /// <summary>Positive: Current resolves to the ALC that actually owns this assembly —
    /// AssemblyLoadContext.GetLoadContext on EngineLoadContext's OWN assembly, not a
    /// hardcoded reference to Default that would happen to look identical in every test
    /// host today (every test host's "this assembly's ALC" IS Default, so a test that
    /// only asserted `EngineLoadContext.Current == AssemblyLoadContext.Default` would
    /// pass unchanged if Current were rewritten to hardcode Default — asserting equality
    /// against the independently-computed GetLoadContext call closes that gap).</summary>
    [Fact]
    public void Current_ResolvesToThisAssemblysOwnLoadContext()
    {
        var expected = AssemblyLoadContext.GetLoadContext(typeof(EngineLoadContext).Assembly);
        Assert.NotNull(expected);
        Assert.Same(expected, EngineLoadContext.Current);
    }

    /// <summary>Positive + the actual regression pin: LoadFromBytes lands a freshly
    /// in-memory-compiled assembly in EngineLoadContext.Current specifically — proving
    /// the mechanism (AssemblyLoadContext.LoadFromStream, which loads into a SPECIFIC ALC
    /// instance) rather than the static Assembly.Load(byte[]) overload it replaces, which
    /// is documented .NET behaviour to always bind into Default regardless of caller (see
    /// #2030, filed alongside this consolidation for the 6 call sites that still do
    /// that). A gutted LoadFromBytes that called Assembly.Load(bytes) internally would
    /// still return SOME loaded assembly (so a bare "didn't throw" test would pass) but
    /// its ALC would not equal Current the moment Current itself differs from Default —
    /// this asserts the ALC identity directly, not just that loading succeeded.</summary>
    [Fact]
    public void LoadFromBytes_LoadsIntoCurrentEngineLoadContext()
    {
        var bytes = CompileTrivialAssembly(
            "EngineLoadContextTests_Probe_" + Guid.NewGuid().ToString("N"));

        var loaded = EngineLoadContext.LoadFromBytes(bytes);

        var loadedContext = AssemblyLoadContext.GetLoadContext(loaded);
        Assert.NotNull(loadedContext);
        Assert.Same(EngineLoadContext.Current, loadedContext);
    }
}
