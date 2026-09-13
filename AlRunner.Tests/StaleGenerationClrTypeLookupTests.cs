// StaleGenerationClrTypeLookupTests — issue #4099, review of PR #4101.
//
// RecordPatches.FindClrTypeByName answers NCLMetaApplicationObject.ApplicationObjectClrType for
// pages, pageextensions, reports and codeunits. On a server/watch reload it is reached with the
// previous generation of Page{id} and PageExtension{id} still loaded, but no AL-visible effect of
// that answer was found, so this pins the lookup directly: two loaded assemblies with one simple
// name, the second registered as current, and the lookup must answer from the second.

using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

public sealed class StaleGenerationClrTypeLookupTests
{
    private static byte[] Compile(string assemblyName, string source)
    {
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(
                Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
        };
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source) },
            refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success,
            string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return ms.ToArray();
    }

    private static Type? FindClrTypeByName(string name)
    {
        var method = typeof(AlRunner.Patches.RecordPatches).GetMethod(
            "FindClrTypeByName", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (Type?)method!.Invoke(null, new object[] { name });
    }

    [Fact]
    public void FindClrTypeByName_SkipsThePreviousGenerationOfAModule()
    {
        // A name no other assembly in the test host declares, and one simple name for both generations.
        var suffix = Guid.NewGuid().ToString("N");
        var typeName = "Page64099" + suffix.Substring(0, 8);
        var assemblyName = "al-runner-4099-gen-" + suffix;
        string Source(int generation) =>
            $"public class {typeName} {{ public const int Generation = {generation}; }}";

        var first = Assembly.Load(Compile(assemblyName, Source(1)));
        var second = Assembly.Load(Compile(assemblyName, Source(2)));
        Assert.NotSame(first, second);

        BcRuntime.RegisterAssemblyGeneration(first);
        BcRuntime.RegisterAssemblyGeneration(second);
        Assert.True(BcRuntime.IsStaleBundleAssembly(first));
        Assert.False(BcRuntime.IsStaleBundleAssembly(second));

        var found = FindClrTypeByName(typeName);

        Assert.NotNull(found);
        var generation = (int)found!.GetField("Generation")!.GetRawConstantValue()!;
        Assert.Equal(2, generation);
    }
}
