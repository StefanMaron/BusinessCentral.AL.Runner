// Pins the C# walk behind #4868: BcRuntime.GetCallerCallstackModules, which answers
// NavApp.GetCallerCallstackModuleInfos from the managed stack. The AL-observable claim is corpus
// codeunit 67595; these tests drive the walk over two Roslyn-compiled stand-in "apps", each
// registered as a module the way DependencyLoader registers a real one.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AlRunner;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

public sealed class CallerCallstackModulesTests
{
    private static readonly Lazy<(Guid A, Guid B, MethodInfo[] Entry)> Apps = new(Build);

    // App B: Ask runs the probe (so Ask is the asking AL method); Outer reaches Ask through a
    // second frame of B. App A calls into B, directly or through a second frame of A.
    private const string SourceB = @"
using System; using System.Runtime.CompilerServices;
public static class CodeunitB {
    [MethodImpl(MethodImplOptions.NoInlining)] public static object Ask(Func<object> p) { var r = p(); return r; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static object Outer(Func<object> p) { var r = Ask(p); return r; }
}";

    private const string SourceA = @"
using System; using System.Runtime.CompilerServices;
public static class CodeunitA {
    [MethodImpl(MethodImplOptions.NoInlining)] public static object CallAsk(Func<object> p) { var r = CodeunitB.Ask(p); return r; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static object CallOuter(Func<object> p) { var r = CodeunitB.Outer(p); return r; }
    [MethodImpl(MethodImplOptions.NoInlining)] public static object CallAskTwice(Func<object> p) { var r = CallAsk(p); return r; }
}";

    private static byte[] Compile(string name, string source, params MetadataReference[] extra)
    {
        var dir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(dir, "System.Runtime.dll")),
        };
        refs.AddRange(extra);
        // Debug: no opportunistic tail calls, so every stand-in method keeps its own frame.
        var compilation = CSharpCompilation.Create(name, new[] { CSharpSyntaxTree.ParseText(source) }, refs,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Debug));
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success,
            string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return ms.ToArray();
    }

    private static (Guid, Guid, MethodInfo[]) Build()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var bBytes = Compile("CallstackAppB_" + suffix, SourceB);
        var b = Assembly.Load(bBytes);
        // A byte-loaded assembly is not found by name, so A's reference to B resolves here.
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) => e.Name == b.FullName ? b : null;
        var a = Assembly.Load(Compile("CallstackAppA_" + suffix, SourceA, MetadataReference.CreateFromImage(bBytes)));
        Guid appA = Guid.NewGuid(), appB = Guid.NewGuid();
        BcRuntime.RegisterModuleInfoForAssembly(a, appA, "Callstack App A", "Probe", "1.0.0.0");
        BcRuntime.RegisterModuleInfoForAssembly(b, appB, "Callstack App B", "Probe", "2.0.0.0");
        var typeA = a.GetType("CodeunitA")!;
        return (appA, appB, new[] { "CallAsk", "CallOuter", "CallAskTwice" }
            .Select(n => typeA.GetMethod(n)!).ToArray());
    }

    private static List<Guid> AskVia(string entry)
    {
        var method = Apps.Value.Entry.Single(m => m.Name == entry);
        Func<object> probe = () => BcRuntime.GetCallerCallstackModules().Select(m => m.AppId).ToList();
        return (List<Guid>)method.Invoke(null, new object[] { probe })!;
    }

    [Fact]
    public void CallFromOtherApp_ListsTheCallingApp_NotTheAskingApp()
    {
        var (appA, _, _) = Apps.Value;
        Assert.Equal(new[] { appA }, AskVia("CallAsk"));
    }

    [Fact]
    public void AskingAppWithAnotherFrame_IsListed_BeforeItsCaller()
    {
        var (appA, appB, _) = Apps.Value;
        Assert.Equal(new[] { appB, appA }, AskVia("CallOuter"));
    }

    [Fact]
    public void AppWithTwoFrames_IsListedOnce()
    {
        var (appA, _, _) = Apps.Value;
        Assert.Equal(new[] { appA }, AskVia("CallAskTwice"));
    }

    [Fact]
    public void GetCallerModuleInfo_SharesTheWalk_AndStillAnswersTheImmediateCaller()
    {
        var (appA, appB, _) = Apps.Value;
        var method = Apps.Value.Entry.Single(m => m.Name == "CallOuter");
        Func<object> probe = () => BcRuntime.GetCallerModuleFromCallStack().AppId;
        // Ask's immediate caller is Outer, in B itself — BC does not walk on to a foreign frame.
        Assert.Equal(appB, (Guid)method.Invoke(null, new object[] { probe })!);
        method = Apps.Value.Entry.Single(m => m.Name == "CallAsk");
        Assert.Equal(appA, (Guid)method.Invoke(null, new object[] { probe })!);
    }
}
