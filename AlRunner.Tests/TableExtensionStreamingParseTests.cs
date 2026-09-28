// BcAppSymbolCache.ParseTableExtensions reads SymbolReference.json token by token and builds a
// JsonDocument per TableExtensions element only (#4945). The whole-document JsonDocument it
// replaced rented its buffers from ArrayPool<byte>.Shared, which kept about 0.5 GB of them for
// the rest of the run on Base Application.
//
// Two properties, tested separately:
//  - EQUIVALENCE: every fixture below is parsed by the streaming reader and by a reference that
//    is the pre-#4945 code path (ReadSymbolReferences + JsonDocument.Parse + the old traversal,
//    calling the same TryParseTableExtensionSymbol), and the two results must serialize
//    identically — same entries, same order, same first-wins dedup, same failures.
//  - ALLOCATION: parsing a large document allocates far less than the document's size.
//
// Synthetic packages built here, so no platform artifacts and no Base Application floor
// (.claude/rules/no-base-app-in-csharp-tests.md).

using System.IO.Compression;
using System.Reflection;
using System.Text;
using System.Text.Json;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Same collection as BcAppSymbolCacheTableExtTests (#1821): GetTableExtensions consults the
// process-global CacheRoots override.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class TableExtensionStreamingParseTests : IDisposable
{
    private readonly string _dir = TestScratch.Dir("al-runner-4945-tableext-streaming");

    public TableExtensionStreamingParseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private static readonly Type Cache = typeof(BcAppSymbolCache);

    private static MethodInfo Private(string name, params Type[] args)
    {
        var m = Cache.GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static, args);
        Assert.True(m != null, $"BcAppSymbolCache.{name} not found — this test's reference path needs it");
        return m!;
    }

    private static readonly MethodInfo ParseTableExtensionsMethod = Private("ParseTableExtensions", typeof(string));
    private static readonly MethodInfo ReadSymbolReferencesMethod = Private("ReadSymbolReferences", typeof(string));
    private static readonly MethodInfo TryParseOneMethod = Private("TryParseTableExtensionSymbol", typeof(JsonElement));

    private static IReadOnlyList<TableExtensionSymbol> Streaming(string appPath)
    {
        try { return (IReadOnlyList<TableExtensionSymbol>)ParseTableExtensionsMethod.Invoke(null, [appPath])!; }
        catch (TargetInvocationException tie) when (tie.InnerException != null) { throw tie.InnerException; }
    }

    /// <summary>The pre-#4945 ParseTableExtensions, verbatim apart from reflection.</summary>
    private static IReadOnlyList<TableExtensionSymbol> Reference(string appPath)
    {
        var result = new Dictionary<int, TableExtensionSymbol>();
        try
        {
            foreach (var json in (IEnumerable<string>)ReadSymbolReferencesMethod.Invoke(null, [appPath])!)
            {
                using var doc = JsonDocument.Parse(json);
                Visit(doc.RootElement, result);
            }
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException { InnerException: { } i } ? i : ex;
            throw new BcAppSymbolReadException(appPath, "table extensions", inner);
        }
        return result.Values.ToList();

        static void Visit(JsonElement container, Dictionary<int, TableExtensionSymbol> exts)
        {
            if (container.TryGetProperty("TableExtensions", out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var ext in arr.EnumerateArray())
                {
                    var parsed = (TableExtensionSymbol?)TryParseOneMethod.Invoke(null, [ext]);
                    if (parsed != null && !exts.ContainsKey(parsed.ExtensionId))
                        exts[parsed.ExtensionId] = parsed;
                }
            if (container.TryGetProperty("Namespaces", out var nss) && nss.ValueKind == JsonValueKind.Array)
                foreach (var ns in nss.EnumerateArray())
                    Visit(ns, exts);
        }
    }

    private static readonly JsonSerializerOptions Deep = new() { IncludeFields = true, WriteIndented = true };

    /// <summary>Runs both; asserts identical output, or that both refuse. Returns the output.</summary>
    private static IReadOnlyList<TableExtensionSymbol>? AssertEquivalent(string appPath)
    {
        IReadOnlyList<TableExtensionSymbol>? expected = null, actual = null;
        Exception? expectedError = null, actualError = null;
        try { expected = Reference(appPath); } catch (Exception e) { expectedError = e; }
        try { actual = Streaming(appPath); } catch (Exception e) { actualError = e; }

        if (expectedError != null || actualError != null)
        {
            Assert.True(expectedError != null,
                $"streaming refused what the whole-document parse accepts: {actualError}");
            Assert.True(actualError != null,
                $"streaming accepted what the whole-document parse refuses ({expectedError?.InnerException?.GetType().Name}); got {actual?.Count} entries");
            Assert.IsType<BcAppSymbolReadException>(expectedError);
            Assert.IsType<BcAppSymbolReadException>(actualError);
            return null;
        }
        Assert.Equal(JsonSerializer.Serialize(expected, Deep), JsonSerializer.Serialize(actual, Deep));
        return actual;
    }

    private string WriteApp(string json, Encoding? encoding = null) =>
        WriteRaw(ZipWith(("SymbolReference.json", (encoding ?? new UTF8Encoding(false)).GetPreamble()
            .Concat((encoding ?? new UTF8Encoding(false)).GetBytes(json)).ToArray())));

    private string WriteRaw(byte[] bytes)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".app");
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] ZipWith(params (string Name, byte[] Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var s = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                s.Write(content);
            }
        return ms.ToArray();
    }

    private static string Ext(int id, string target, string fields = "[]", string extra = "", string? name = null) => $$"""
        { {{extra}} "TargetObject": {{JsonSerializer.Serialize(target)}}, "Fields": {{fields}}, "Id": {{id}}, "Name": {{JsonSerializer.Serialize(name ?? $"Ext{id}")}} }
        """;

    private const string RichFields = """
        [
          { "TypeDefinition": { "Name": "Code[20]" }, "Properties": [
              { "Name": "Caption", "Value": "Shïp \"to\" \\ code \/ x" },
              { "Name": "TableRelation", "Value": "\"Ship-to Address\".Code WHERE (\"Customer No.\"=FIELD(\"No.\"))" },
              { "Name": "ValidateTableRelation", "Value": "0" },
              { "Name": "DataClassification", "Value": "CustomerContent" } ],
            "Id": 50100, "Name": "Ship\ttab" },
          { "TypeDefinition": { "Name": "Decimal" }, "Properties": [
              { "Name": "FieldClass", "Value": "FlowField" },
              { "Name": "CalcFormula", "Value": "Sum(\"Cust. Ledger Entry\".Amount WHERE (\"Customer No.\"=FIELD(\"No.\")))" },
              { "Name": "Editable", "Value": "0" },
              { "Name": "MinValue", "Value": "0" }, { "Name": "MaxValue", "Value": "100" } ],
            "Id": 50101, "Name": "Balance" },
          { "TypeDefinition": { "Name": "Option" }, "Properties": [
              { "Name": "OptionMembers", "Value": " ,Open,Closed" },
              { "Name": "OptionCaption", "Value": " ,Open,Closed" },
              { "Name": "InitValue", "Value": "Open" } ],
            "Id": 50102, "Name": "State" },
          { "TypeDefinition": { "Name": "Date" }, "Properties": [ { "Name": "FieldClass", "Value": "FlowFilter" } ],
            "Id": 50103, "Name": "Date Filter" },
          { "TypeDefinition": { "Name": "Integer" }, "Properties": [ { "Name": "AutoIncrement", "Value": "1" } ],
            "Id": 50104 },
          { "TypeDefinition": { "Name": "Code[10]" }, "Name": "NoId" },
          { "Properties": [], "Id": 50105, "Name": "NoTypeDefinition" }
        ]
        """;

    private const string Keys = """
        "Keys": [
          { "Name": "Key12", "FieldNames": [ "Ship\ttab", "", "Balance" ] },
          { "Name": "Empty", "FieldNames": [] },
          { "FieldNames": [ "State" ] },
          { "Name": "NoFieldNames" }
        ],
        "Properties": [ { "Name": "DataClassification", "Value": "SystemMetadata" } ],
        """;

    /// <summary>
    /// Every shape the parser handles in one package: TableExtensions after Namespaces in the
    /// same object, extensions at three nesting levels, one id declared twice (the traversal
    /// order, not the text order, decides which wins), escaped strings, missing optional
    /// properties, unrelated large members to skip, and a TableExtensions value that is not an
    /// array.
    /// </summary>
    [Fact]
    public void EveryShape_SameAsTheWholeDocumentParse()
    {
        var app = WriteApp($$"""
            {
              "RuntimeVersion": "15.1",
              "AppId": "f3552374-a1f2-4356-848e-196002525837",
              "Namespaces": [
                {
                  "Name": "Microsoft",
                  "Tables": [ { "Id": 18, "Name": "Customer", "Fields": [ { "Id": 1, "Name": "No." } ] } ],
                  "Namespaces": [
                    { "Name": "Sales", "TableExtensions": [ {{Ext(7, "#f3552374a1f24356848e196002525837#Deep Dup", name: "DeepSeven")}} ],
                      "Namespaces": [ { "Name": "Deeper", "TableExtensions": [ {{Ext(8, "Deepest")}} ] } ] },
                    { "Name": "Empty" },
                    { "Name": "NotAnArray", "TableExtensions": { "Id": 99 } },
                    { "Name": "Null", "TableExtensions": null, "Namespaces": null }
                  ],
                  "TableExtensions": [
                    {{Ext(5, "#f3552374a1f24356848e196002525837#Customer", RichFields, Keys)}},
                    {{Ext(6, "Item", """[ { "Id": 1, "Name": "A" } ]""")}}
                  ]
                },
                { "Name": "Second", "TableExtensions": [ {{Ext(9, "Vendor")}}, {{Ext(7, "Loses To Namespace Order")}} ] }
              ],
              "Codeunits": [ { "Id": 1, "Name": "x", "Methods": [ [1, 2, [3, { "a": [true, false, null] }]], -1.5e10 ] } ],
              "TableExtensions": [
                {{Ext(7, "Root Wins Dup", name: "RootSeven")}},
                {{Ext(10, "#nohash")}},
                {{Ext(11, "")}},
                { "Id": 12, "Name": "NoTarget" },
                { "Name": "NoId", "TargetObject": "X" },
                { "Id": 13, "TargetObject": "No Name" },
                { "Id": 14, "Name": null, "TargetObject": "Null Name" },
                {{Ext(15, "café 😀 \"quoted\"")}}
              ]
            }
            """);

        var result = AssertEquivalent(app);

        // Pin the order and the dedup explicitly too, so a reader need not trust the reference.
        Assert.NotNull(result);
        Assert.Equal(new[] { 7, 10, 13, 14, 15, 5, 6, 8, 9 }, result!.Select(e => e.ExtensionId));
        Assert.Equal("RootSeven", result[0].ExtensionName);
        Assert.Equal("#nohash", result[1].TargetTableName);
        Assert.Equal("TableExt14", result.Single(e => e.ExtensionId == 14).ExtensionName);
        Assert.Equal("café \U0001F600 \"quoted\"", result.Single(e => e.ExtensionId == 15).TargetTableName);
        var five = result.Single(e => e.ExtensionId == 5);
        Assert.Equal("Customer", five.TargetTableName);
        Assert.Equal("Shïp \"to\" \\ code / x", five.Fields.Single(f => f.FieldId == 50100).Caption);
        Assert.Equal("Ship\ttab", five.Fields.Single(f => f.FieldId == 50100).FieldName);
        Assert.Contains(50101, five.CalcFormulaTexts!.Keys);
        Assert.Equal(new[] { "Key12", "Key" }, five.Keys!.Select(k => k.Name));
    }

    /// <summary>A property that appears twice in one object: the JSON parse keeps one of them.</summary>
    [Fact]
    public void DuplicatePropertyNames_SameAsTheWholeDocumentParse()
    {
        AssertEquivalent(WriteApp($$"""
            {
              "TableExtensions": [ {{Ext(1, "First")}} ],
              "Namespaces": [ { "TableExtensions": [ {{Ext(2, "Ns First")}} ] } ],
              "TableExtensions": [ {{Ext(3, "Second")}} ],
              "Namespaces": [ { "TableExtensions": [ {{Ext(4, "Ns Second")}} ] } ]
            }
            """));
        // A poisoned entry in the property that is NOT kept must not fail the parse either way.
        AssertEquivalent(WriteApp($$"""
            {
              "TableExtensions": [ 42, { "Id": "text" } ],
              "Namespaces": [ 1, [ 2 ] ],
              "TableExtensions": [ {{Ext(3, "Kept")}} ],
              "Namespaces": [ { "TableExtensions": [ {{Ext(4, "Kept Ns")}} ] } ]
            }
            """));
        AssertEquivalent(WriteApp($$"""
            {
              "TableExtensions": [ {{Ext(3, "Kept")}} ],
              "TableExtensions": [ 42 ]
            }
            """));
    }

    [Theory]
    [InlineData("""{ "TableExtensions": [ 42 ] }""")]                                   // element is not an object
    [InlineData("""{ "TableExtensions": [ { "Id": "12", "TargetObject": "X" } ] }""")]  // Id is not a number
    [InlineData("""{ "Namespaces": [ 7 ] }""")]                                          // namespace is not an object
    [InlineData("""[ { "TableExtensions": [] } ]""")]                                  // root is not an object
    [InlineData("""{ "TableExtensions": [] } { }""")]                                   // trailing value
    [InlineData("""{ "Tables": [ 1, 2, ] }""")]                                         // invalid JSON, in a skipped member
    [InlineData("""{ /* comment */ "TableExtensions": [] }""")]                        // comments are not JSON
    [InlineData("""{ "TableExtensions": [ """)]                                         // truncated
    [InlineData("")]                                                                     // empty
    [InlineData("""{ "TableExtensions": [] }""")]                                       // the plain case, for contrast
    public void Refusals_SameAsTheWholeDocumentParse(string json) => AssertEquivalent(WriteApp(json));

    [Fact]
    public void ByteOrderMarks_SameAsTheWholeDocumentParse()
    {
        var json = $$"""{ "TableExtensions": [ {{Ext(1, "Café")}}, {{Ext(2, "😀")}} ] }""";
        foreach (var enc in new Encoding[]
                 {
                     new UTF8Encoding(true), new UTF8Encoding(false), new UnicodeEncoding(false, true),
                     new UnicodeEncoding(true, true), new UTF32Encoding(false, true), new UTF32Encoding(true, true),
                 })
        {
            var result = AssertEquivalent(WriteApp(json, enc));
            Assert.Equal(new[] { "Café", "\U0001F600" }, result!.Select(e => e.TargetTableName));
        }
    }

    /// <summary>
    /// The reader holds a fixed window of the document, so elements straddle its edge and one
    /// element here is larger than the whole window. Thousands of extensions, some deduplicated,
    /// spread across namespaces.
    /// </summary>
    [Fact]
    public void ElementsAcrossReadBoundaries_SameAsTheWholeDocumentParse()
    {
        var huge = "[" + string.Join(",", Enumerable.Range(1, 4000).Select(i =>
            $$"""{ "TypeDefinition": { "Name": "Text[100]" }, "Properties": [ { "Name": "Caption", "Value": "Field é {{i}}" } ], "Id": {{i}}, "Name": "F{{i}}" }""")) + "]";
        var sb = new StringBuilder("{ \"Namespaces\": [");
        for (var ns = 0; ns < 40; ns++)
        {
            if (ns > 0) sb.Append(',');
            sb.Append($$"""{ "Name": "N{{ns}}", "Tables": [ {{string.Join(",", Enumerable.Repeat("""{ "Id": 1, "Name": "T" }""", 50))}} ], "TableExtensions": [""");
            sb.Append(string.Join(",", Enumerable.Range(0, 100).Select(i => Ext(ns * 90 + i, $"Target {ns}/{i}", RichFields, Keys))));
            sb.Append("] }");
        }
        sb.Append($$"""], "TableExtensions": [ {{Ext(999999, "Huge", huge)}} ] }""");
        Assert.True(sb.Length > 4 * 256 * 1024, $"fixture is only {sb.Length} chars");
        Assert.True(huge.Length > 256 * 1024, $"the large element is only {huge.Length} chars");

        var result = AssertEquivalent(WriteApp(sb.ToString()));
        Assert.Equal(4000, result!.First().Fields.Count);
        Assert.Equal(1 + 39 * 90 + 100, result.Count);
    }

    /// <summary>
    /// The R2R wrapper: the outer package's own SymbolReference.json comes first, the nested
    /// root-level .app's second, and an id both declare keeps the outer one.
    /// </summary>
    [Fact]
    public void NestedPackage_SameAsTheWholeDocumentParse()
    {
        var inner = ZipWith(("SymbolReference.json", Encoding.UTF8.GetBytes(
            $$"""{ "TableExtensions": [ {{Ext(1, "Inner Dup")}}, {{Ext(2, "Inner Only")}} ] }""")));
        var app = WriteRaw(ZipWith(
            ("SymbolReference.json", Encoding.UTF8.GetBytes($$"""{ "TableExtensions": [ {{Ext(1, "Outer")}} ] }""")),
            ("sub/Ignored.app", Encoding.UTF8.GetBytes("not read")),
            ("Inner.app", inner)));

        var result = AssertEquivalent(app);
        Assert.Equal(new[] { "Outer", "Inner Only" }, result!.Select(e => e.TargetTableName));
    }

    /// <summary>
    /// The property #4945 is about. A whole-document parse holds the entire document at once
    /// (the old path held it as a string, twice its UTF-8 size, before the JsonDocument's own
    /// buffers). Reading it token by token allocates on the order of the table extensions it
    /// returns. The padding compresses well, so the package on disk stays small and what is
    /// measured is the parse, not the file read.
    /// </summary>
    [Fact]
    public void LargeDocument_AllocatesFarLessThanItsSize()
    {
        const int targetSize = 32 * 1024 * 1024;
        var table = """{ "Id": 18, "Name": "Customer \"x\"", "Fields": [ { "Id": 1, "Name": "No.", "TypeDefinition": { "Name": "Code[20]" }, "Properties": [ { "Name": "Caption", "Value": "No." } ] } ] }""";
        var sb = new StringBuilder(targetSize + 4096);
        sb.Append($$"""{ "Namespaces": [ { "Name": "Microsoft", "TableExtensions": [ {{Ext(2, "Item", RichFields, Keys)}} ], "Tables": [ """);
        sb.Append(table);
        while (sb.Length < targetSize) sb.Append(", ").Append(table);
        sb.Append($$""" ] } ], "TableExtensions": [ {{Ext(1, "Customer", RichFields)}} ] }""");
        var utf8 = Encoding.UTF8.GetBytes(sb.ToString());
        sb.Clear();
        var app = WriteRaw(ZipWith(("SymbolReference.json", utf8)));
        var documentSize = utf8.Length;
        utf8 = null;

        // Warm-up so JIT and first-call allocations are not charged to the measured call.
        Streaming(WriteApp($$"""{ "TableExtensions": [ {{Ext(3, "Warm", RichFields, Keys)}} ] }"""));

        var before = GC.GetAllocatedBytesForCurrentThread();
        var result = Streaming(app);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(new[] { 1, 2 }, result.Select(e => e.ExtensionId));
        Assert.True(allocated < documentSize / 8,
            $"ParseTableExtensions allocated {allocated:N0} bytes for a {documentSize:N0}-byte SymbolReference.json declaring two table extensions");
    }
}
