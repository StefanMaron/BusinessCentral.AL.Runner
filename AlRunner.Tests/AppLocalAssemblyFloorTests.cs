// #5030: an assembly the runner deploys app-local (its bin, listed in deps.json) owns the default
// AssemblyLoadContext's slot for that simple name. When the selected BC build binds a HIGHER version
// of it, the default binder refuses, and DependencyLoader's resolver cannot serve BC's own newer copy
// either — LoadFromAssemblyPath into the default context throws FileLoadException for a name the
// app-local set already holds. BC 28 builds 28.x.*.554xx raised System.Diagnostics.EventLog from
// 8.0.0.0 to 10.0.0.0 and the runner died at startup in SuppressEventLogWriter (exit 134).
//
// So the app-local copy of every non-BC assembly must be at least the version the selected build's
// artifacts reference. Microsoft.Dynamics.* is excluded: the resolver serves the selected build's copy
// of those whatever version a caller asks for (ResolvedAssemblyVersionGuard).

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using AlRunner.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

public sealed class AppLocalAssemblyFloorTests
{
    /// <summary>
    /// Each reference from a DLL in <paramref name="artifactDir"/> to an assembly present in
    /// <paramref name="appLocalDir"/> at a LOWER version, as "name app-local=X wanted=Y by Z".
    /// </summary>
    internal static List<string> FloorViolations(string appLocalDir, string artifactDir)
    {
        var appLocal = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(appLocalDir, "*.dll"))
            if (ReadIdentity(path) is { } id) appLocal[id.Name] = id.Version;

        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(artifactDir, "*.dll"))
        {
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) continue;
            var md = pe.GetMetadataReader();
            foreach (var handle in md.AssemblyReferences)
            {
                var reference = md.GetAssemblyReference(handle);
                var name = md.GetString(reference.Name);
                if (name.StartsWith("Microsoft.Dynamics.", StringComparison.Ordinal)) continue;
                if (appLocal.TryGetValue(name, out var have) && reference.Version > have)
                    violations.Add($"{name} app-local={have} wanted={reference.Version} by {Path.GetFileName(path)}");
            }
        }
        return violations;
    }

    private static (string Name, Version Version)? ReadIdentity(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        if (!pe.HasMetadata) return null;
        var md = pe.GetMetadataReader();
        if (!md.IsAssembly) return null;
        var def = md.GetAssemblyDefinition();
        return (md.GetString(def.Name), def.Version);
    }

    [SkippableFact]
    public void TheSelectedBuildReferencesNoAppLocalAssemblyAboveTheVersionTheRunnerDeploys()
    {
        TestArtifacts.SkipIfMissing();
        var built = BcArtifacts.EngineBuiltVersion();
        Assert.NotNull(built);
        var artifactDir = BcArtifacts.ArtifactDirFor(built!.ToString());
        TestArtifacts.SkipIf(!Directory.Exists(artifactDir),
            $"the built BC version's artifact dir '{artifactDir}' is not provisioned.");

        // Control: the scan reads the dir it was pointed at — Types binds EventLog on every build.
        var eventLog = Path.Combine(AppContext.BaseDirectory, "System.Diagnostics.EventLog.dll");
        Assert.True(File.Exists(eventLog), $"no app-local System.Diagnostics.EventLog at '{eventLog}'");

        var violations = FloorViolations(AppContext.BaseDirectory, artifactDir);
        Assert.True(violations.Count == 0,
            $"BC {built} binds assemblies above the runner's app-local copies, which the default load "
            + "context then cannot load (#5030). Raise the PackageReference in AlRunner.csproj:\n  "
            + string.Join("\n  ", violations));
    }

    [Fact]
    public void FloorViolations_ReportsAReferenceAboveTheAppLocalCopy_AndNothingElse()
    {
        var root = TestScratch.Dir("al-runner-5030-floor");
        try
        {
            var appLocal = Directory.CreateDirectory(Path.Combine(root, "bin")).FullName;
            var artifacts = Directory.CreateDirectory(Path.Combine(root, "artifacts")).FullName;

            Emit("Floor.Lib", "1.0.0.0", "public class L {}", Path.Combine(appLocal, "Floor.Lib.dll"));
            Emit("Microsoft.Dynamics.Floor", "1.0.0.0", "public class D {}", Path.Combine(appLocal, "Microsoft.Dynamics.Floor.dll"));

            var lib2 = Emit("Floor.Lib", "2.0.0.0", "public class L {}", null);
            var lib1 = Emit("Floor.Lib", "1.0.0.0", "public class L {}", null);
            var dyn2 = Emit("Microsoft.Dynamics.Floor", "2.0.0.0", "public class D {}", null);
            Emit("Above", "1.0.0.0", "public class A { public L F; }", Path.Combine(artifacts, "Above.dll"), lib2);
            Emit("Equal", "1.0.0.0", "public class E { public L F; }", Path.Combine(artifacts, "Equal.dll"), lib1);
            Emit("Platform", "1.0.0.0", "public class P { public D F; }", Path.Combine(artifacts, "Platform.dll"), dyn2);

            var violations = FloorViolations(appLocal, artifacts);
            Assert.Equal(new[] { "Floor.Lib app-local=1.0.0.0 wanted=2.0.0.0 by Above.dll" }, violations);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    public void SuppressEventLogWriter_InstallsTheNoOpWriter_OnTheBuiltTypesAssembly()
    {
        var types = Assembly.Load(new AssemblyName("Microsoft.Dynamics.Nav.Types"));
        var prop = types.GetType("Microsoft.Dynamics.Nav.Types.EventLogWriter", throwOnError: true)!
            .GetProperty("CustomWriter", BindingFlags.Public | BindingFlags.Static)!;
        prop.SetValue(null, null);
        Assert.Null(prop.GetValue(null));

        BcRuntime.SuppressEventLogWriter(types);

        var writer = Assert.IsAssignableFrom<Delegate>(prop.GetValue(null));
        Assert.Equal("EventLogNoOpDyn", writer.Method.Name);
        Assert.Equal("System.Diagnostics.EventLog", writer.Method.GetParameters()[1].ParameterType.Assembly.GetName().Name);
    }

    [Theory]
    [InlineData(false, "Microsoft.Dynamics.Nav.Types")]
    [InlineData(true, "EventLogWriter")]
    public void SuppressEventLogWriter_RefusesAsAShapeGap_WhenTheBindIsAbsent(bool passAnAssembly, string member)
    {
        var ex = Assert.Throws<BcShapeGapException>(
            () => BcRuntime.SuppressEventLogWriter(passAnAssembly ? typeof(object).Assembly : null));
        Assert.Equal(member, ex.Member);
    }

    private static MetadataReference Emit(
        string name, string version, string source, string? outputPath, MetadataReference? reference = null)
    {
        var refs = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator)
            .Where(p => Path.GetFileName(p) is "System.Runtime.dll" or "System.Private.CoreLib.dll")
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();
        if (reference != null) refs.Add(reference);
        var compilation = CSharpCompilation.Create(name,
            new[] { CSharpSyntaxTree.ParseText($"[assembly: System.Reflection.AssemblyVersion(\"{version}\")] {source}") },
            refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        if (outputPath != null) File.WriteAllBytes(outputPath, ms.ToArray());
        return MetadataReference.CreateFromImage(ms.ToArray());
    }
}
