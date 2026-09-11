// EnumExtensibleAndImplementationRenderTests — #3807.
//
// Four members BC's ObjectMetadataEmitter states about an enum and the runner's
// SymbolReference.json derivation did not:
//
//     MetaEnum.Extensible                    31 of 142 base enums, answered false for all 31
//     MetaEnumValue.InterfaceImplementation  32 values, answered [] for all 32
//     MetaEnum.DefaultImplementation          3 enums
//     MetaEnum.UnknownImplementation          1 enum
//
// The last three were already PARSED (#2306) and merely never rendered; Extensible was not
// carried by EnumSymbol at all, so AlEnumMetadataRegistry.Entry had nothing to state and BC's
// MetaEnum(XmlNode) applied its own default of false.
//
// THE OPEN QUESTION THE ISSUE RAISED, AND HOW IT WAS SETTLED
//   #3807 asked whether BC's <Value> shape can express InterfaceImplementation at all, or
//   whether only the emitter's own form carries it. Types.Metadata.MetaEnumValue(XmlNode)
//   answers it: the constructor reads an `Implementation` ATTRIBUTE on <Value> into
//   InterfaceImplementation, and MetaEnum(XmlNode) reads `Extensible`, `DefaultImplementation`
//   and `UnknownImplementation` as attributes on the <Enum> root. All four are expressible.
//   Derivation and the per-binary counts: the PR body for #3807.
//
// TWO SPELLINGS THAT ARE NOT INTERCHANGEABLE, both read off those constructors:
//   * Extensible goes through MetaBase.Int32Value, which is Int32.Parse with "" and "undefined"
//     mapped to 0. So the attribute must be "1"/"0"; writing "true" makes BC THROW, not default.
//   * The implementation lists are split on MetaBase.SplitChar and Int32.Parse'd per part, so a
//     comma-joined id list is the shape — the same one SymbolReference.json states.
//
// This is a RUNNER-MECHANISM test, not a BC-behaviour claim: it pins that the runner's own
// derivation and render carry these values. It reads the render back through BC's own
// MetaEnum(XmlNode) — resolved by reflection, exactly as
// MetadataEquivalenceReportEnumPermissionSetOracleTests does — so the assertions are about
// what BC sees rather than about our XML text.
using System.Reflection;
using System.Xml;
using AlRunner;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class EnumExtensibleAndImplementationRenderTests
{
    private readonly BcEngineFixture _engine;

    public EnumExtensibleAndImplementationRenderTests(BcEngineFixture engine) => _engine = engine;

    private static Type MetaEnumType()
        => Type.GetType("Microsoft.Dynamics.Nav.Types.Metadata.MetaEnum, Microsoft.Dynamics.Nav.Types")
           ?? throw new InvalidOperationException("MetaEnum is not reachable.");

    /// <summary>The runner's render, read back through BC's own <c>MetaEnum(XmlNode)</c>.</summary>
    private static object ReadBackThroughBc(XmlElement root)
    {
        var ctor = MetaEnumType().GetConstructor(new[] { typeof(XmlNode) })
                   ?? throw new InvalidOperationException("MetaEnum has no (XmlNode) constructor.");
        return ctor.Invoke(new object?[] { root })!;
    }

    private static int[] IntArray(object parsed, string property)
    {
        var value = parsed.GetType().GetProperty(property)!.GetValue(parsed)!;
        return ((System.Collections.IEnumerable)value).Cast<int>().ToArray();
    }

    /// <summary>Every <c>MetaEnumValue</c> the parse produced, keyed by its <c>Ordinal</c> —
    /// never by position: BC's emitter writes an enum's values in NAME order, so index and
    /// ordinal coincide only by accident.</summary>
    private static Dictionary<int, object> ValuesByOrdinal(object parsed)
    {
        var values = parsed.GetType().GetProperty("Values")!.GetValue(parsed)!;
        var result = new Dictionary<int, object>();
        foreach (var v in (System.Collections.IEnumerable)values)
        {
            var ordinal = (int)v.GetType().GetProperty("Ordinal")!.GetValue(v)!;
            result[ordinal] = v;
        }
        return result;
    }

    private static int[] InterfaceImplementation(object metaEnumValue)
    {
        var value = metaEnumValue.GetType().GetProperty("InterfaceImplementation")!.GetValue(metaEnumValue)!;
        return ((System.Collections.IEnumerable)value).Cast<int>().ToArray();
    }

    private static string? Name(object metaEnumValue)
        => (string?)metaEnumValue.GetType().GetProperty("Name")!.GetValue(metaEnumValue);

    // The shape of a real System Application enum that declares all four: Extensible = 1, a
    // per-value Implementation, and both enum-level fallbacks. Modelled on enum 1465
    // "Encryption Algorithm", the issue's named example for DefaultImplementation and
    // UnknownImplementation, widened with Extensible so one document covers all four members.
    private const string SymbolJson = """
        {
          "Id": 1465,
          "Name": "Encryption Algorithm",
          "Properties": [
            { "Name": "Extensible", "Value": "1" },
            { "Name": "DefaultImplementation", "Value": "1467" },
            { "Name": "UnknownValueImplementation", "Value": "1467" }
          ],
          "Values": [
            { "Name": "Aes", "Ordinal": 0,
              "Properties": [ { "Name": "Implementation", "Value": "1467" } ] },
            { "Name": "TripleDES", "Ordinal": 1,
              "Properties": [ { "Name": "Implementation", "Value": "1468" } ] },
            { "Name": "NoImplementer", "Ordinal": 2 }
          ]
        }
        """;

    // The same enum with Extensible declared FALSE — the 99-of-129 case in 28.1, and the
    // negative direction that stops "write 1 unconditionally" passing.
    private const string NotExtensibleJson = """
        {
          "Id": 9005,
          "Name": "User Plan Experience",
          "Properties": [ { "Name": "Extensible", "Value": "0" } ],
          "Values": [ { "Name": "Basic", "Ordinal": 0 } ]
        }
        """;

    // An enum declaring NO Extensible property at all — 12 of 142 in 28.1. AL's own default is
    // false (CodeAnalysis ApplicationObjectTypeSymbol.ExtensibleByDefault => false, with no enum
    // override), which is also MetaEnum's default, so absent and "0" must agree.
    private const string SilentJson = """
        {
          "Id": 60001,
          "Name": "Declares Nothing",
          "Values": [ { "Name": "Only", "Ordinal": 0 } ]
        }
        """;

    private static BcAppSymbolCache.EnumSymbol Parse(string json)
    {
        var parsed = BcAppSymbolCache.ParseEnumSymbolForTest(
            System.Text.Json.JsonDocument.Parse(json).RootElement);
        Assert.NotNull(parsed);
        return parsed!;
    }

    /// <summary>Registers a parsed symbol exactly as the precompiled-dependency path does
    /// (RecordPatches.AddBcAppPath), then renders it.</summary>
    private static XmlElement RegisterAndRender(BcAppSymbolCache.EnumSymbol e)
    {
        AlEnumMetadataRegistry.Register(
            e.Id, e.Name, e.Options.ToArray(), e.Indexes.ToArray(),
            e.Implementations.Select(i => i.ToArray()).ToArray(),
            e.Captions?.ToArray(),
            e.DefaultImplementations?.ToArray(),
            e.UnknownImplementations?.ToArray(),
            e.Extensible);

        var xml = RecordPatches.TryBuildEnumMetadataEquivalenceXml(e.Id);
        Assert.NotNull(xml);
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        return doc.DocumentElement!;
    }

    /// <summary>
    /// The parse arm: <c>Extensible</c> reaches <see cref="BcAppSymbolCache.EnumSymbol"/> at all.
    /// Before #3807 the record had no such member, so this is the member that did not exist.
    /// Needs no BC engine — it reads JSON only.
    /// </summary>
    [Fact]
    public void TheSymbolParse_CarriesExtensible_InAllThreeStates()
    {
        Assert.True(Parse(SymbolJson).Extensible);
        Assert.False(Parse(NotExtensibleJson).Extensible);
        // The THIRD state, and the one a plain bool would destroy: an enum declaring the
        // property not at all is null, not false. BC's own emitter keeps the distinction —
        // 12 of its 144 enum documents state no Extensible attribute — so folding null into
        // false here would make the render state an attribute BC leaves off.
        Assert.Null(Parse(SilentJson).Extensible);

        // The three that were already parsed, asserted here so a regression in the parse is
        // distinguishable from a regression in the render below.
        var e = Parse(SymbolJson);
        Assert.Equal(new[] { 1467 }, e.DefaultImplementations);
        Assert.Equal(new[] { 1467 }, e.UnknownImplementations);
        Assert.Equal(new[] { 1467 }, e.Implementations[0]);
        Assert.Equal(new[] { 1468 }, e.Implementations[1]);
        Assert.Empty(e.Implementations[2]);
    }

    /// <summary>
    /// The render arm, read back through BC's OWN <c>MetaEnum(XmlNode)</c> — the same
    /// constructor the metadata-equivalence harness reads both sides with, so a passing
    /// assertion here is a statement about what BC sees, not about our XML text.
    /// </summary>
    [SkippableFact]
    public void TheRender_StatesAllFourMembers_AsBcsOwnReaderSeesThem()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);
        AlEnumMetadataRegistry.Clear();
        try
        {
            var parsed = ReadBackThroughBc(RegisterAndRender(Parse(SymbolJson)));

            // The three enum-level members, as BC's own reader answers them.
            Assert.True((bool)parsed.GetType().GetProperty("Extensible")!.GetValue(parsed)!);
            Assert.Equal(new[] { 1467 }, IntArray(parsed, "DefaultImplementation"));
            Assert.Equal(new[] { 1467 }, IntArray(parsed, "UnknownImplementation"));

            var byOrdinal = ValuesByOrdinal(parsed);
            Assert.Equal(new[] { 1467 }, InterfaceImplementation(byOrdinal[0]));
            Assert.Equal(new[] { 1468 }, InterfaceImplementation(byOrdinal[1]));
            // A value declaring none stays EMPTY — "declares none" is a different statement
            // from "declares 0", and writing a 0 would make BC resolve codeunit 0.
            Assert.Empty(InterfaceImplementation(byOrdinal[2]));

            // The names survive alongside, so a render that dropped the Values subtree while
            // adding attributes cannot pass.
            Assert.Equal("Aes", Name(byOrdinal[0]));
            Assert.Equal("TripleDES", Name(byOrdinal[1]));
            Assert.Equal(3, byOrdinal.Count);
        }
        finally
        {
            AlEnumMetadataRegistry.Clear();
        }
    }

    /// <summary>
    /// The negative direction of the render: an enum that declares none of the four must leave
    /// every one of them OFF the document, so BC applies its own default rather than reading a
    /// value the runner manufactured. This is what stops "always write Extensible=1" and
    /// "always write Implementation=0" from passing the test above.
    /// </summary>
    [SkippableFact]
    public void TheRender_LeavesUndeclaredMembersOff_RatherThanStatingADefault()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);
        AlEnumMetadataRegistry.Clear();
        try
        {
            var root = RegisterAndRender(Parse(SilentJson));

            // Not merely "BC answers false" — the ATTRIBUTE is absent, which is the
            // distinction between deriving a value and letting BC default it. An always-write
            // render would answer false here too, and this is the assertion that catches it.
            Assert.False(root.HasAttribute("Extensible"));
            Assert.False(root.HasAttribute("DefaultImplementation"));
            Assert.False(root.HasAttribute("UnknownImplementation"));
            Assert.False(((XmlElement)root.SelectSingleNode("Values/Value")!).HasAttribute("Implementation"));

            var parsed = ReadBackThroughBc(root);
            Assert.False((bool)parsed.GetType().GetProperty("Extensible")!.GetValue(parsed)!);
            Assert.Empty(IntArray(parsed, "DefaultImplementation"));
            Assert.Empty(IntArray(parsed, "UnknownImplementation"));
            Assert.Empty(InterfaceImplementation(ValuesByOrdinal(parsed)[0]));
        }
        finally
        {
            AlEnumMetadataRegistry.Clear();
        }
    }

    /// <summary>
    /// An enum declaring <c>Extensible = 0</c> states the attribute and BC reads false from it —
    /// the direction that separates "derived false" from "defaulted false". Together with the
    /// test above, these pin that all three states of the property (true / false / undeclared)
    /// survive to BC distinctly.
    /// </summary>
    [SkippableFact]
    public void AnExplicitlyNonExtensibleEnum_StatesZero_AndBcReadsFalse()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);
        AlEnumMetadataRegistry.Clear();
        try
        {
            var root = RegisterAndRender(Parse(NotExtensibleJson));
            Assert.Equal("0", root.GetAttribute("Extensible"));

            var parsed = ReadBackThroughBc(root);
            Assert.False((bool)parsed.GetType().GetProperty("Extensible")!.GetValue(parsed)!);
        }
        finally
        {
            AlEnumMetadataRegistry.Clear();
        }
    }

    /// <summary>
    /// The render's shape against BC's OWN emitter documents, which is the only evidence that
    /// distinguishes "the attribute is absent" from "the attribute says 0". It is the mistake
    /// this test exists to hold: an unconditional <c>Extensible</c> answers false for an
    /// undeclared enum, agrees with BC's default, and is still wrong — BC's emitter omits the
    /// attribute on 12 of its 144 enum documents, so writing "0" there manufactures a
    /// difference the harness would report as the runner's.
    ///
    /// <para>Measured against whatever bundle this box carries rather than against a literal,
    /// so the claim stays true on every BC build. Skips when no bundle is present.</para>
    /// </summary>
    [SkippableFact]
    public void TheEmittersOwnDocuments_OmitExtensibleOnSomeEnums_AndStateImplementationOnValues()
    {
        var bundles = MetadataEquivalenceBundleGate.RequireBundles();

        var declared = 0;
        var undeclared = 0;
        var valuesWithImplementation = 0;
        foreach (var bundle in bundles)
            foreach (var obj in bundle.Objects.Where(o => o.Kind == "Enum"))
            {
                var doc = new XmlDocument();
                doc.Load(Path.Combine(bundle.Directory, obj.File));
                var root = doc.DocumentElement!;
                // A base enum only: an enumextension states bare <Value> children and no
                // Extensible, and counting those as "undeclared base enums" would make the
                // assertion below pass for the wrong reason.
                if (root.GetElementsByTagName("Values").Count == 0) continue;

                if (root.HasAttribute("Extensible")) declared++; else undeclared++;
                foreach (XmlNode v in root.GetElementsByTagName("Value"))
                    if (v is XmlElement ve && ve.HasAttribute("Implementation")) valuesWithImplementation++;
            }

        Skip.If(declared + undeclared == 0, "no bundle on this box carries a base-enum document.");

        // Both directions are real, which is what makes the render's conditional correct:
        // some base enums state the attribute and some state none.
        Assert.True(declared > 0, "no emitted base enum states Extensible — the render's '1'/'0' has no referent.");
        Assert.True(undeclared > 0,
            "every emitted base enum states Extensible, so an unconditional render would be " +
            "faithful and this test's reason for existing is gone. Re-read the emitter output.");

        // And the answer to #3807's open question, in BC's own output rather than in the
        // decompiled reader: <Value> DOES carry Implementation.
        Assert.True(valuesWithImplementation > 0,
            "no emitted enum value states Implementation — the per-value render has no referent.");
    }

    /// <summary>
    /// <c>Extensible</c> must be written as <c>"1"</c>, never <c>"true"</c>: BC parses it with
    /// <c>MetaBase.Int32Value</c>, which is <c>Int32.Parse</c> with only <c>""</c> and
    /// <c>"undefined"</c> mapped to 0. A <c>"true"</c> would make BC's constructor THROW, so the
    /// spelling is load-bearing and a boolean-looking render fails loudly rather than quietly
    /// answering false. Needs no BC engine — it reads the runner's own XML text.
    /// </summary>
    [Fact]
    public void Extensible_IsWrittenAsOne_BecauseBcInt32ParsesIt()
    {
        AlEnumMetadataRegistry.Clear();
        try
        {
            Assert.Equal("1", RegisterAndRender(Parse(SymbolJson)).GetAttribute("Extensible"));
        }
        finally
        {
            AlEnumMetadataRegistry.Clear();
        }
    }
}
