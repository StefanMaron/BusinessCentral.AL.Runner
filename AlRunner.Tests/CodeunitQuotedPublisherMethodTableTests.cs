// CodeunitQuotedPublisherMethodTableTests — a precompiled codeunit whose event publisher is declared
// with a quoted AL name keeps its <Methods> subtree (#5200).
//
// THE DEFECT
//   RecordPatches.ReadCodeunitFacts named each [NavEvent] publisher by its C# method name, and the
//   symbol file lists AL names. The compiler rewrites a quoted name into a legal C# identifier
//   ("On Before Quoted" -> On_Before_Quoted), so the two differ and the codeunit was withheld with
//   "publisher 'On_Before_Quoted' is not in the symbol file". The publisher's AL name is the
//   [NavName] on its <method>_Scope class.
//
// WHY THE FIXTURE IS BUILT HERE
//   No shipped Microsoft app declares a mangled publisher (#5200, "Not measured"), so the route is
//   reached only by an app built for the purpose. Every input below is BC's own output for one AL
//   source: the symbol file is BcCompiler.EmitDepSymbols (BC's SymbolReference writer), the assembly
//   is Emit's C# compiled by BcAssembler — the step `--precompile` runs, which is what a service tier
//   does before it ReadyToRun-compiles the same C# — and the expected <Methods> subtree is the
//   metadata document BC's emitter produced in that same Emit. The package is the zip shape
//   AppLoader.ExtractAllDlls reads: SymbolReference.json plus publishedartifacts/*.dll.
//
//   The one stand-in is the last test's assembly: it needs a shape BC never emits (a [NavEvent]
//   method with no scope class), so it is a hand-written C# type that declares only the three
//   attribute names the reader matches on.

using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CodeunitQuotedPublisherMethodTableTests : IDisposable
{
    private const string MetaNs = "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects";
    private const int HostId = 88520001;
    private const int StandInId = 88520002;

    private static readonly Guid AppId = new("5200c0de-0000-4000-8000-000000000001");

    // Two publishers whose C# names differ from their AL names — one with a space only
    // (On_Before_Quoted), one with every kind of punctuation (Ona45Checka46Value_a40Qtya41_a38_Amt) —
    // one that does not, a subscriber that must interleave between them, and a plain procedure BC
    // does not emit.
    private const string HostSource = """
        codeunit 88520001 "QP Quoted Publisher Host"
        {
            procedure PlainProcedure(): Integer
            begin
                exit(1);
            end;

            [IntegrationEvent(false, false)]
            procedure "On Before Quoted"(var Handled: Boolean)
            begin
            end;

            [EventSubscriber(ObjectType::Codeunit, Codeunit::"QP Quoted Publisher Host", 'On Before Quoted', '', false, false)]
            local procedure HandleBeforeQuoted(var Handled: Boolean)
            begin
            end;

            [IntegrationEvent(false, false)]
            procedure "On-Check.Value (Qty) & Amt"(Qty: Decimal)
            begin
            end;

            [BusinessEvent(false)]
            procedure OnPlainPublisher()
            begin
            end;
        }
        """;

    private static readonly string[] ExpectedMethodNames =
    {
        "On Before Quoted", "HandleBeforeQuoted", "On-Check.Value (Qty) & Amt", "OnPlainPublisher",
    };

    private readonly string _root;
    private readonly BcEngineFixture _engine;

    public CodeunitQuotedPublisherMethodTableTests(BcEngineFixture engine)
    {
        _engine = engine;
        _root = TestScratch.Dir("al-runner-codeunit-quoted-publisher-5200");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void RequireEngine()
        => TestArtifacts.SkipIf(!_engine.Ready, _engine.SkipReason ?? "the in-process BC engine is not ready (see BcEngineCollection).");

    /// <summary>The precompiled package, and the document BC's own emitter wrote for the codeunit.</summary>
    private (string AppPath, XmlDocument BcDocument) BuildPrecompiledApp()
    {
        File.WriteAllText(Path.Combine(_root, "Host.al"), HostSource);

        var output = new BcCompiler().Emit(new[] { _root }, "QuotedPublisherModule");
        Assert.Empty(output.Diagnostics);
        var compile = new BcAssembler().Compile("QuotedPublisherModule", output.Sources);
        Assert.True(compile.Success, string.Join("\n", compile.Errors));

        Assert.True(AlObjectMetadataRegistry.TryGet("Codeunit", HostId, out var bcXml),
            "BC's emitter document for the host codeunit was not captured by Emit");
        var bcDocument = new XmlDocument();
        bcDocument.LoadXml(bcXml);

        var symbolsPath = Path.Combine(_root, "SymbolReference.json");
        new BcCompiler().EmitDepSymbols(new[] { _root }, "QuotedPublisherModule", AppId, "AL Runner",
            new Version(1, 0, 0, 0), symbolsPath);

        var appPath = Path.Combine(_root, "quoted-publisher.app");
        using (var fs = new FileStream(appPath, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(symbolsPath, "SymbolReference.json");
            var dll = zip.CreateEntry("publishedartifacts/QuotedPublisherModule.dll");
            using var s = dll.Open();
            s.Write(compile.AssemblyBytes!, 0, compile.AssemblyBytes!.Length);
        }
        return (appPath, bcDocument);
    }

    private static XmlElement? MethodsOf(XmlDocument doc)
        => doc.DocumentElement!.GetElementsByTagName("Methods", MetaNs).OfType<XmlElement>().FirstOrDefault();

    private static XmlDocument? RunnerDocument(int id)
    {
        var xml = RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(id);
        if (xml is null) return null;
        var d = new XmlDocument();
        d.LoadXml(xml);
        return d;
    }

    /// <summary>Element names and attributes, attribute order ignored — the reader's view.</summary>
    private static string Canonical(XmlElement e)
        => e.LocalName + "{" + string.Join(" ", e.Attributes.OfType<XmlAttribute>()
               .Where(a => a.Prefix != "xmlns" && a.Name != "xmlns")
               .OrderBy(a => a.LocalName, StringComparer.Ordinal).Select(a => a.LocalName + "=" + a.Value)) + "}"
           + string.Concat(e.ChildNodes.OfType<XmlElement>().Select(Canonical));

    private static List<BcAppSymbolCache.CodeunitMethodSymbol> Publishers(params string[] names)
        => names.Select((n, i) => new BcAppSymbolCache.CodeunitMethodSymbol(
            2000 + i, n, "EventPublisherAttribute", "IntegrationEvent", Parameters: [])).ToList();

    /// <summary>
    /// The population claim for the fixture: the runner emits the subtree, and it is BC's exactly —
    /// four methods in source order, the subscriber between its two neighbouring publishers, each
    /// named by its AL name.
    /// </summary>
    [SkippableFact]
    public void A_precompiled_codeunit_with_quoted_publishers_emits_BCs_exact_methods_subtree()
    {
        RequireEngine();
        var (appPath, bcDocument) = BuildPrecompiledApp();
        RecordPatches.ResetForReload();
        RecordPatches.AddBcAppPath(appPath);

        var bc = MethodsOf(bcDocument);
        Assert.NotNull(bc);
        Assert.Equal(ExpectedMethodNames,
            bc!.ChildNodes.OfType<XmlElement>().Select(m => m.GetAttribute("Name")).ToArray());

        var doc = RunnerDocument(HostId);
        Assert.True(doc is not null, "the runner knows no codeunit " + HostId);
        var mine = MethodsOf(doc!);
        Assert.True(mine is not null,
            "the runner withheld the <Methods> subtree: "
            + RecordPatches.CodeunitMethodTableRefusalForTests(appPath, HostId, null));
        Assert.Equal(Canonical(bc), Canonical(mine!));
    }

    /// <summary>
    /// The refusal still fires, and names the publisher the way the symbol file does: an omitted
    /// quoted publisher withholds the codeunit, saying <c>On Before Quoted</c> rather than its C#
    /// spelling. The control — every publisher present — is accepted, and an omitted unquoted
    /// publisher is named exactly as before.
    /// </summary>
    [SkippableTheory]
    [InlineData("On Before Quoted")]
    [InlineData("On-Check.Value (Qty) & Amt")]
    [InlineData("OnPlainPublisher")]
    public void An_omitted_publisher_withholds_the_codeunit_and_is_named_by_its_AL_name(string omitted)
    {
        RequireEngine();
        var (appPath, _) = BuildPrecompiledApp();
        var all = new[] { "On Before Quoted", "On-Check.Value (Qty) & Amt", "OnPlainPublisher" };

        Assert.Null(RecordPatches.CodeunitMethodTableRefusalForTests(appPath, HostId, Publishers(all)));
        Assert.Equal($"publisher '{omitted}' is not in the symbol file",
            RecordPatches.CodeunitMethodTableRefusalForTests(appPath, HostId,
                Publishers(all.Where(n => n != omitted).ToArray())));
    }

    /// <summary>
    /// A [NavEvent] method with no <c>&lt;method&gt;_Scope</c> class cannot be renamed, so it keeps
    /// the name it had before this change — its C# method name — rather than being refused for a
    /// shape no population of codeunits was measured to need. BC never emits this shape, so the
    /// assembly is a stand-in (see this file's header).
    /// </summary>
    [SkippableFact]
    public void A_publisher_with_no_scope_class_keeps_its_method_name()
    {
        RequireEngine();
        var appPath = Path.Combine(_root, "no-scope.app");
        var dll = CompileStandIn("""
            using System;
            public sealed class NavEventAttribute : Attribute { }
            public sealed class Codeunit88520002 { [NavEvent] public void OnNoScope() { } }
            """);
        using (var fs = new FileStream(appPath, FileMode.Create))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using var s = zip.CreateEntry("publishedartifacts/StandIn.dll").Open();
            s.Write(dll, 0, dll.Length);
        }

        Assert.Null(RecordPatches.CodeunitMethodTableRefusalForTests(appPath, StandInId, Publishers("OnNoScope")));
        Assert.Equal("publisher 'OnNoScope' is not in the symbol file",
            RecordPatches.CodeunitMethodTableRefusalForTests(appPath, StandInId, []));
    }

    private static byte[] CompileStandIn(string source)
    {
        var compilation = CSharpCompilation.Create("QuotedPublisherStandIn",
            new[] { CSharpSyntaxTree.ParseText(source) },
            new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return ms.ToArray();
    }
}
