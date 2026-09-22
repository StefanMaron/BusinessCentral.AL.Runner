// CodeunitMethodParameterRenderingTests — the <Parameters> shapes the SHIPPED Microsoft apps
// cannot exercise, driven from a fixture symbol file (#4084).
//
// WHY A SECOND FILE, AND WHAT IT MEASURES THAT THE FIRST CANNOT
//   CodeunitMethodParameterDerivationTests joins BC's own emitter output to the shipped apps'
//   SymbolReference.json, which is what licenses the two mappings — 1,308 parameter observations
//   over four distinct Ncl.dll binaries, all exact. That join can only measure shapes Microsoft
//   ships on an EMITTED method, and two of the derivation's decisions have no such shape:
//
//     the REFUSAL path   every parameter in both apps is renderable (zero unmapped AL types,
//                        and the one array-typed parameter in either app -- codeunit 9556
//                        GetRecordsFromTableId, "Text" with "ArrayDimensions": [10] -- sits on
//                        a method carrying NO attribute, so BC never emits it)
//     the EMPTY element  BC writes <Parameters /> for a method declaring none, and the symbol
//                        file states no "Parameters" key at all for those 15 of 152
//
//   Measured while writing this file: the mutation that SKIPS an unrenderable parameter instead
//   of withdrawing the element ran GREEN against the join (Failed: 0, Passed: 2), because no
//   input in the shipped apps ever reaches that branch. That is a genuine absorption, not a weak
//   assertion -- and it is what this file exists to red (tdd.md: an absorbed mutation can mean
//   the observable is coarse, so fix the fixture until the mutated term decides).
//
// WHY REFUSING IS THE RIGHT ANSWER RATHER THAN OMITTING
//   MetadataObjectDiff pairs children POSITIONALLY. A <Parameters> element missing the one
//   parameter the derivation cannot spell puts every later parameter in a different slot, which
//   is the runner asserting an association it has no evidence for -- worse than stating nothing
//   (loud-failures.md, and the same judgement #3788 made one level up for <Methods>).

using System.IO.Compression;
using System.Text;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Reaches the RecordPatches AL parse statics, which are process-wide (#1696, #1712).
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class CodeunitMethodParameterRenderingTests : IDisposable
{
    private const string MetaNs = "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects";

    /// <summary>Parameters of every shape the derivation renders: a scalar, a var scalar, a
    /// length-carrying scalar, a record, a var record and an interface.</summary>
    private const int RenderableParameters = 61091;

    /// <summary>A method declaring NO parameter, which BC writes as an empty element rather than
    /// omitting it.</summary>
    private const int NoParameters = 61092;

    /// <summary>A method one of whose parameters is an AL type the derivation has not measured.
    /// The WHOLE element is withdrawn, not the one parameter.</summary>
    private const int UnmeasuredType = 61093;

    /// <summary>A method one of whose parameters is an ARRAY, which BC writes with
    /// <c>IsArray="True"</c> and a runtime type nothing here has observed.</summary>
    private const int ArrayParameter = 61094;

    private readonly string _root;

    public CodeunitMethodParameterRenderingTests()
    {
        _root = TestScratch.Dir("al-runner-method-parameter-rendering");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // Every AL spelling here is the one Microsoft's own symbol files use for that shape, copied
    // from Business Foundation / System Application 28.1.49838.53910 rather than invented: a
    // record states TypeDefinition.Subtype.{Id,Name}, an interface states a Subtype with a Name
    // and NO Id, a length rides inside the type name as Code[20], and `var` is "IsVar": true.
    private static readonly string SymbolReference = $$"""
        {
          "RuntimeVersion": "15.1",
          "AppId": "b2e4c1a7-5d3f-4e8b-9c0a-1f2e3d4c5b6a",
          "Name": "Method Parameter Fixture",
          "Namespaces": [
            {
              "Name": "Fixture",
              "Codeunits": [
                {
                  "Id": {{RenderableParameters}},
                  "Name": "Renderable Parameters",
                  "Properties": [],
                  "Methods": [
                    { "Id": 2101, "Name": "OnEveryShape",
                      "Attributes": [ { "Name": "IntegrationEvent" } ],
                      "Parameters": [
                        { "Name": "HideErrorsAndWarnings",
                          "TypeDefinition": { "Name": "Boolean" } },
                        { "IsVar": true, "Name": "ResultCount",
                          "TypeDefinition": { "Name": "Integer" } },
                        { "Name": "NoSeriesCode",
                          "TypeDefinition": { "Name": "Code[20]" } },
                        { "Name": "SentEmail",
                          "TypeDefinition": { "Name": "Record",
                            "Subtype": { "Id": 8889, "Name": "Sent Email" } } },
                        { "IsVar": true, "Name": "NoSeriesLine",
                          "TypeDefinition": { "Name": "Record",
                            "Subtype": { "Id": 309, "Name": "No. Series Line" } } },
                        { "IsVar": true, "Name": "AADObjectID",
                          "TypeDefinition": { "Name": "Text" } },
                        { "IsVar": true, "Name": "FeatureDataUpdate",
                          "TypeDefinition": { "Name": "Interface",
                            "Subtype": { "Name": "Feature Data Update" } } },
                        { "IsVar": true, "Name": "PrimaryKeys",
                          "TypeDefinition": { "Name": "List",
                            "TypeArguments": [ { "Name": "Code[250]" } ] } }
                      ] }
                  ]
                },
                {
                  "Id": {{NoParameters}},
                  "Name": "No Parameters",
                  "Properties": [],
                  "Methods": [
                    { "Id": 2201, "Name": "OnNothingAtAll",
                      "Attributes": [ { "Name": "IntegrationEvent" } ] }
                  ]
                },
                {
                  "Id": {{UnmeasuredType}},
                  "Name": "Unmeasured Type",
                  "Properties": [],
                  "Methods": [
                    { "Id": 2301, "Name": "OnUnmeasuredMiddleParameter",
                      "Attributes": [ { "Name": "IntegrationEvent" } ],
                      "Parameters": [
                        { "Name": "FirstRenderable",
                          "TypeDefinition": { "Name": "Boolean" } },
                        { "Name": "SomethingNew",
                          "TypeDefinition": { "Name": "TypeNobodyHasMeasured" } },
                        { "Name": "LastRenderable",
                          "TypeDefinition": { "Name": "Integer" } }
                      ] }
                  ]
                },
                {
                  "Id": {{ArrayParameter}},
                  "Name": "Array Parameter",
                  "Properties": [],
                  "Methods": [
                    { "Id": 2401, "Name": "OnArrayParameter",
                      "Attributes": [ { "Name": "IntegrationEvent" } ],
                      "Parameters": [
                        { "Name": "FirstRenderable",
                          "TypeDefinition": { "Name": "Boolean" } },
                        { "IsVar": true, "Name": "PrimaryKeyCaptions",
                          "TypeDefinition": { "Name": "Text", "ArrayDimensions": [ 10 ] } }
                      ] }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private void Register()
    {
        var appPath = Path.Combine(_root, "method-parameters.app");
        using (var zip = new FileStream(appPath, FileMode.Create))
        using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
        {
            var entry = za.CreateEntry("SymbolReference.json");
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write(SymbolReference);
        }

        RecordPatches.ResetForReload();
        RecordPatches.ClearCodeunitSubscriberWitnessForTests();
        RecordPatches.AddBcAppPath(appPath);
        // The witness clears every fixture codeunit: none declares a subscriber, so the
        // <Methods> subtree is derived and the parameters under it are reachable at all.
        RecordPatches.RegisterCodeunitSubscriberWitness(
            appPath, Array.Empty<int>(),
            new[] { RenderableParameters, NoParameters, UnmeasuredType, ArrayParameter });
    }

    private static XmlElement Projection(int codeunitId)
    {
        var xml = RecordPatches.TryBuildCodeunitMetadataEquivalenceXml(codeunitId);
        Assert.True(xml is not null, $"the runner derived no metadata for codeunit {codeunitId}");
        var doc = new XmlDocument();
        doc.LoadXml(xml!);
        return doc.DocumentElement!;
    }

    /// <summary>The <c>&lt;Parameters&gt;</c> element of the codeunit's single method, or null
    /// when none was written — which is the refusal, and is deliberately distinct from an element
    /// with no children.</summary>
    private static XmlElement? ParametersElement(int codeunitId)
    {
        var methods = Projection(codeunitId)
            .GetElementsByTagName("Methods", MetaNs).OfType<XmlElement>().FirstOrDefault();
        Assert.True(methods is not null,
            $"codeunit {codeunitId} rendered no <Methods> subtree, so this test cannot reach the " +
            "parameters it is about — the witness registration or the fixture has changed shape.");
        var method = methods!.ChildNodes.OfType<XmlElement>().First(e => e.LocalName == "Method");
        return method.ChildNodes.OfType<XmlElement>().FirstOrDefault(e => e.LocalName == "Parameters");
    }

    /// <summary>Each <c>&lt;Parameter&gt;</c> as the six values BC writes, in document order,
    /// with <c>Length</c> rendered as <c>-</c> where the attribute is ABSENT — so an absent
    /// length and a written zero cannot read the same in a failure message.</summary>
    private static List<string> Parameters(int codeunitId)
    {
        var parameters = ParametersElement(codeunitId);
        Assert.True(parameters is not null,
            $"codeunit {codeunitId} rendered no <Parameters> element at all.");
        return parameters!.ChildNodes.OfType<XmlElement>()
            .Where(e => e.LocalName == "Parameter")
            .Select(e => string.Join(" ",
                $"IsVar={e.GetAttribute("IsVar")}",
                $"IsArray={e.GetAttribute("IsArray")}",
                $"Name={e.GetAttribute("Name")}",
                $"RuntimeAttributes='{e.GetAttribute("RuntimeAttributes")}'",
                $"RuntimeType={e.GetAttribute("RuntimeType")}",
                $"Length={(e.HasAttribute("Length") ? e.GetAttribute("Length") : "-")}"))
            .ToList();
    }

    /// <summary>
    /// What a refusal assertion reports when it FAILS. A separate method on purpose: both
    /// refusal tests previously built this string as an <c>Assert.True</c> argument, which C#
    /// evaluates BEFORE the call — so the diagnostic dereferenced the very null the assertion was
    /// there to confirm and the PASSING path threw NullReferenceException. Measured: both tests
    /// failed with an NRE against a correct implementation.
    /// </summary>
    private static string Rendered(XmlElement parameters)
        => string.Join(" | ", parameters.ChildNodes.OfType<XmlElement>()
            .Select(e => $"{e.GetAttribute("Name")}:{e.GetAttribute("RuntimeType")}"
                         + $":IsArray={e.GetAttribute("IsArray")}"));

    /// <summary>
    /// The positive case, asserting every value on every shape rather than a count: BC's own
    /// spelling for a scalar, a var scalar, a length-carrying scalar, a record, a var record, a
    /// first-character-lowercased multi-capital identifier, an interface and a generic list.
    ///
    /// <para>The three shapes that are not what a reader would guess are all here on purpose: a
    /// var RECORD stays <c>INavRecordHandle</c> and takes <c>,[NavByReferenceAttribute]</c>
    /// rather than becoming <c>ByRef&lt;&gt;</c>; an INTERFACE takes neither attribute even
    /// though it is <c>var</c>; and a <c>List of [Code[250]]</c> loses the argument's length.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_renderable_shape_is_written_the_way_BC_writes_it()
    {
        Register();

        Assert.Equal(new[]
        {
            "IsVar=False IsArray=False Name=hideErrorsAndWarnings RuntimeAttributes='' RuntimeType=bool Length=-",
            "IsVar=True IsArray=False Name=resultCount RuntimeAttributes='' RuntimeType=ByRef<int> Length=-",
            "IsVar=False IsArray=False Name=noSeriesCode RuntimeAttributes='' RuntimeType=NavCode Length=20",
            "IsVar=False IsArray=False Name=sentEmail RuntimeAttributes='[NavObjectId(ObjectId=8889)]' RuntimeType=INavRecordHandle Length=-",
            "IsVar=True IsArray=False Name=noSeriesLine RuntimeAttributes='[NavObjectId(ObjectId=309)],[NavByReferenceAttribute]' RuntimeType=INavRecordHandle Length=-",
            "IsVar=True IsArray=False Name=aADObjectID RuntimeAttributes='' RuntimeType=ByRef<NavText> Length=-",
            "IsVar=True IsArray=False Name=featureDataUpdate RuntimeAttributes='' RuntimeType=NavInterfaceHandle Length=-",
            "IsVar=True IsArray=False Name=primaryKeys RuntimeAttributes='' RuntimeType=ByRef<NavList<NavCode>> Length=-",
        }, Parameters(RenderableParameters));
    }

    /// <summary>
    /// A method declaring no parameter gets the EMPTY element BC writes, not a withdrawn one —
    /// 15 of the 152 methods the runner emits at 28.1.49838.53910 are in this state, and
    /// withdrawing the element for them would turn a closed difference back into an open one.
    ///
    /// <para>The symbol file states no <c>Parameters</c> key at all for such a method (measured
    /// on all 15), so "absent key" and "declares none" are the same input here, and the empty
    /// element is what distinguishes it from a refusal.</para>
    /// </summary>
    [Fact]
    public void A_method_declaring_no_parameter_gets_the_empty_element_BC_writes()
    {
        Register();

        var parameters = ParametersElement(NoParameters);
        Assert.True(parameters is not null,
            "the element was WITHDRAWN for a method that declares no parameter. BC writes " +
            "<Parameters /> for those, so withdrawing it reopens the MetaMethod.Parameters " +
            "difference on every such method.");
        Assert.Empty(parameters!.ChildNodes.OfType<XmlElement>());
    }

    /// <summary>
    /// One parameter the derivation cannot spell withdraws the WHOLE element — the all-or-nothing
    /// rule, and the assertion that catches a "skip what you cannot render" edit.
    ///
    /// <para>The unmeasured parameter is deliberately in the MIDDLE of three, because that is the
    /// case where omitting it is most plausible and most wrong: the two renderable ones on either
    /// side would land in BC's slots 0 and 1 while BC has them in 0 and 2.</para>
    /// </summary>
    [Fact]
    public void One_unmeasured_AL_type_withdraws_the_whole_element_rather_than_skipping_it()
    {
        Register();

        var parameters = ParametersElement(UnmeasuredType);
        if (parameters is not null)
            Assert.Fail(
                "a <Parameters> element was written for a method carrying an AL type the " +
                "derivation has not measured. Whatever it contains is a positional claim the " +
                "runner cannot support: MetadataObjectDiff pairs these children by INDEX, so a " +
                "skipped middle parameter puts the third parameter in BC's second slot. It " +
                "rendered: " + Rendered(parameters));
    }

    /// <summary>
    /// An ARRAY parameter withdraws the element too, and for a reason worth keeping separate from
    /// the unmeasured-type case: the AL type IS one the derivation maps (<c>Text</c>), so a rule
    /// that only checked the type name would render it — with <c>IsArray="False"</c> and a
    /// non-array runtime type, both wrong.
    ///
    /// <para>Not reachable from the shipped apps: the single array-typed parameter in either app
    /// (codeunit 9556 <c>GetRecordsFromTableId</c>) sits on a method carrying no attribute, so BC
    /// emits nothing for it and the join cannot see this branch at all.</para>
    /// </summary>
    [Fact]
    public void An_array_parameter_withdraws_the_element_even_though_its_base_type_maps()
    {
        Register();

        var parameters = ParametersElement(ArrayParameter);
        if (parameters is not null)
            Assert.Fail(
                "a <Parameters> element was written for a method with an ARRAY parameter. BC " +
                "writes IsArray=\"True\" and a runtime type this derivation has never observed, " +
                "so anything written here is unmeasured. It rendered: " + Rendered(parameters));
    }
}
