// SubscriberScopeKeyLoadTimingTests — #5104.
//
// RUNNER-MECHANISM test: the subscriber-scope term of the install-baseline key (#5060) must
// not move when an assembly that declares no [EventSubscriber] loads, or a warm --server
// request misses depending on when a library such as Microsoft.Bcl.AsyncInterfaces happened
// to load. It must still move when an assembly that does declare one loads.
using System.Reflection;
using AlRunner.Patches;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class SubscriberScopeKeyLoadTimingTests
{
    private const string LibrarySource = """
        namespace Fx.Lib
        {
            public class Codeunit90201 { public void Plain() { } }
            public class Helper { public static int Twice(int x) => x * 2; }
        }
        """;

    private const string SubscriberSource = """
        using System;
        namespace Fx.Sub
        {
            public sealed class NavEventSubscriberAttribute : Attribute
            {
                public NavEventSubscriberAttribute(int objectType, int objectId, string methodName) { }
            }
            public class Codeunit90202
            {
                [NavEventSubscriber(1, 18, "OnAfterInsertEvent")]
                public void Handle() { }
            }
        }
        """;

    private static Assembly Load(string prefix, string source)
    {
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(
                Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
        };
        var compilation = CSharpCompilation.Create($"{prefix}-{Guid.NewGuid():N}",
            new[] { CSharpSyntaxTree.ParseText(source) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success,
            string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return Assembly.Load(ms.ToArray());
    }

    [Fact]
    public void LoadingAnAssemblyWithoutSubscribers_LeavesTheKeyAlone()
    {
        var before = EventSubscriberPatches.SubscriberScopeKey();

        var library = Load("al-runner-scope-lib", LibrarySource);

        // It is a scan candidate, so only the subscriber check keeps it out of the key.
        Assert.True(EventSubscriberPatches.IsSubscriberScanCandidate(library));
        Assert.False(EventSubscriberPatches.CanDispatchInto(library));
        Assert.Equal(before, EventSubscriberPatches.SubscriberScopeKey());
    }

    [Fact]
    public void LoadingAnAssemblyWithASubscriber_MovesTheKey()
    {
        var before = EventSubscriberPatches.SubscriberScopeKey();

        var subscriber = Load("al-runner-scope-sub", SubscriberSource);

        Assert.True(EventSubscriberPatches.CanDispatchInto(subscriber));
        var after = EventSubscriberPatches.SubscriberScopeKey();
        Assert.NotEqual(before, after);
        Assert.Contains(subscriber.ManifestModule.ModuleVersionId.ToString("N"), after, StringComparison.Ordinal);
    }
}
