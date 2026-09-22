// CodeunitMethodParameterDerivationTests — the <Parameters> subtree BC writes on every <Method>
// it emits, derived from SymbolReference.json's AL-spelled declaration (#4084).
//
// WHAT #4084 REFUSED TO GUESS, AND WHAT SETTLED IT
//   #3788 landed the <Methods> subtree without <Parameters>, because BC writes the RUNTIME's
//   spelling of a parameter and the symbol file states AL's, and the two differ in both fields.
//   Codeunit 304 GetNoSeriesLine, Business Foundation 28.1.49838.53910:
//
//     BC's <Parameter>                              SymbolReference.json
//     Name="hideErrorsAndWarnings"                  "Name": "HideErrorsAndWarnings"
//     RuntimeType="bool"                            "TypeDefinition": { "Name": "Boolean" }
//     RuntimeType="INavRecordHandle"                "TypeDefinition": { "Name": "Record",
//       RuntimeAttributes="[NavObjectId(ObjectId=309)],[NavByReferenceAttribute]"
//                                                       "Subtype": { "Id": 309, ... } }
//
//   The issue named the hazard precisely: "a casing rule guessed wrong produces a <Parameter>
//   element that looks right and names something else." So both mappings were MEASURED against
//   BC's own emitter output rather than inferred, by joining the ground-truth bundles
//   tools/gen-metadata-ground-truth.sh produces to each app's shipped SymbolReference.json on
//   (codeunit id, method id) — over FOUR builds whose Microsoft.Dynamics.Nav.Ncl.dll are four
//   DISTINCT binaries, so these are four independent measurements and not one wearing four
//   labels (CLAUDE.md's binary-identity rule):
//
//     build               Ncl.dll sha256   joined methods   parameters   exact   disagreements
//     27.5.46862.53931    affa03c9…                   164          322     322               0
//     28.1.49838.53910    49b11d9b…                   168          328     328               0
//     28.1.49838.54308    6f2cf682…                   168          328     328               0
//     28.4.53241.54407    108b8c6b…                   169          330     330               0
//
//   1,308 parameter observations, 1,308 exact on ALL SIX values BC writes — Name, RuntimeType,
//   RuntimeAttributes, IsVar, IsArray and Length's presence and value — zero disagreements and
//   zero AL types the mapping could not spell.
//
// THE CASING RULE IS A PURE FIRST-CHARACTER LOWERCASE, AND THE POPULATION DISCRIMINATES IT
//   Not "camelCase", which is what the eye supplies and which is WRONG here. Three parameters in
//   the shipped apps have two or more leading capitals and each one separates the two rules:
//
//     AL identifier            BC writes               a word-aware rule would write
//     AFSOperationResponse     aFSOperationResponse    afsOperationResponse      <- wrong
//     AADObjectID              aADObjectID             aadObjectID               <- wrong
//     IDataArchiveProvider     iDataArchiveProvider    iDataArchiveProvider         (agrees)
//
//   #4084's own comment had already noticed the shape in the assembly string heap and
//   deliberately declined to close on it, because 14,439 identifiers of every kind are not a
//   parameter population. These three are, and they are what makes the rule measured.
//
// WHY A SHORT LIST IS WORSE THAN NO LIST, WHICH IS WHY THE DERIVATION IS ALL-OR-NOTHING
//   The same property that made #3788's <Methods> subtree all-or-nothing: MetadataObjectDiff
//   pairs children POSITIONALLY. A <Parameters> element that omitted the one parameter it could
//   not spell would put every later parameter in a different slot, which is the runner asserting
//   an association it has no evidence for (loud-failures.md). So one unrenderable parameter
//   withdraws the whole element and the method keeps its honest one-directional absence.
//
// WHY A RUNNER-SIDE MECHANISM TEST AND NO CORPUS TEST
//   The subject is what the runner's SymbolReference.json-derived metadata answers for a
//   PRECOMPILED dependency codeunit. A corpus test compiles its codeunits from source, which
//   takes BC's own compiler and its own metadata path and never reaches this derivation at all —
//   the structural case in bc-behavior-tests-go-upstream.md. BC's emitter output IS the ground
//   truth here, and the metadata-equivalence harness compares against it on every unit-test leg.

using System.Text.Json;
using System.Xml;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CodeunitMethodParameterDerivationTests
{
    private const string MetaNs = "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects";

    /// <summary>One <c>&lt;Parameter&gt;</c> as BC's emitter wrote it — the six values it writes,
    /// with <c>Length</c> null where BC wrote no such attribute (which is not the same as
    /// writing zero).</summary>
    private sealed record EmittedParameter(
        string Name, string RuntimeType, string RuntimeAttributes, bool IsVar, bool IsArray,
        int? Length);

    /// <summary>One <c>&lt;Method&gt;</c> with its parameter list, or null where BC wrote no
    /// <c>&lt;Parameters&gt;</c> element at all — a state that does not occur on the measured
    /// builds and is kept distinct from an EMPTY list, which does.</summary>
    private sealed record EmittedMethodWithParameters(
        int Id, string Name, IReadOnlyList<EmittedParameter>? Parameters);

    private readonly BcEngineFixture _engine;

    public CodeunitMethodParameterDerivationTests(BcEngineFixture engine) => _engine = engine;

    private IReadOnlyList<(GroundTruthBundle Bundle, string App)> Bundles()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);
        var paired = new List<(GroundTruthBundle, string)>();
        foreach (var bundle in MetadataEquivalenceBundleGate.RequireBundles())
        {
            var app = MetadataEquivalenceHarness.FindAppPackage(bundle);
            Assert.True(app is not null,
                $"ground truth exists for {bundle.Label} but its .app is not on this box, so the " +
                "symbol file its parameters must be derived from cannot be read.");
            paired.Add((bundle, app!));
        }
        return paired;
    }

    /// <summary>
    /// Every <c>&lt;Method&gt;</c> of one codeunit document with the parameter list BC wrote for
    /// it, in document order — which is the order the positional differ compares in.
    /// </summary>
    private static IReadOnlyList<EmittedMethodWithParameters> EmittedMethods(string documentPath)
    {
        var doc = new XmlDocument();
        doc.Load(documentPath);
        var methods = doc.DocumentElement!
            .GetElementsByTagName("Methods", MetaNs).OfType<XmlElement>().FirstOrDefault();
        if (methods is null) return Array.Empty<EmittedMethodWithParameters>();

        var result = new List<EmittedMethodWithParameters>();
        foreach (var method in methods.ChildNodes.OfType<XmlElement>())
        {
            if (method.LocalName != "Method") continue;
            var parameters = method.ChildNodes.OfType<XmlElement>()
                .FirstOrDefault(c => c.LocalName == "Parameters");
            List<EmittedParameter>? list = null;
            if (parameters is not null)
            {
                list = new List<EmittedParameter>();
                foreach (var parameter in parameters.ChildNodes.OfType<XmlElement>())
                {
                    if (parameter.LocalName != "Parameter") continue;
                    var lengthText = parameter.GetAttribute("Length");
                    list.Add(new EmittedParameter(
                        parameter.GetAttribute("Name"),
                        parameter.GetAttribute("RuntimeType"),
                        parameter.GetAttribute("RuntimeAttributes"),
                        parameter.GetAttribute("IsVar") == "True",
                        parameter.GetAttribute("IsArray") == "True",
                        string.IsNullOrEmpty(lengthText)
                            ? null
                            : int.Parse(lengthText, System.Globalization.CultureInfo.InvariantCulture)));
                }
            }
            result.Add(new EmittedMethodWithParameters(
                int.Parse(method.GetAttribute("ID"), System.Globalization.CultureInfo.InvariantCulture),
                method.GetAttribute("Name"), list));
        }
        return result;
    }

    /// <summary>
    /// What the RUNNER derives, read through <c>BcAppSymbolCache</c> itself rather than
    /// re-implemented here — so a disagreement is about the shipped derivation and not about a
    /// second copy of it in the test (tdd.md: a test that reconstructs the thing it measures
    /// cannot see it broken).
    /// </summary>
    private static IReadOnlyDictionary<(int Codeunit, int Method), BcAppSymbolCache.CodeunitMethodSymbol>
        DerivedMethods(string appPath)
    {
        var found = new Dictionary<(int, int), BcAppSymbolCache.CodeunitMethodSymbol>();
        foreach (var obj in BcAppSymbolCache.Get(appPath).Objects)
        {
            if (!string.Equals(obj.Kind, "Codeunit", StringComparison.Ordinal)) continue;
            foreach (var method in obj.AttributedMethods ?? new List<BcAppSymbolCache.CodeunitMethodSymbol>())
                found[(obj.Id, method.Id)] = method;
        }
        return found;
    }

    /// <summary>
    /// <b>The measurement that licenses the derivation.</b> For every method BC emits that the
    /// runner's derivation also states, all six values BC writes on each <c>&lt;Parameter&gt;</c>
    /// equal what the derivation answers — name, runtime type, runtime attributes, the var
    /// modifier, the array flag and the length's presence and value.
    ///
    /// <para>Arity is part of it and is asserted first, because a list of the right values in the
    /// wrong length is exactly the positional mis-pairing the all-or-nothing rule exists to
    /// prevent.</para>
    ///
    /// <para>The population is whatever is provisioned on the box; the four builds in this file's
    /// header are what it measured when it was written. Non-vacuity is asserted from the bundle
    /// rather than against a number written down, because a run that joined nothing would
    /// satisfy every assertion above having measured nothing.</para>
    /// </summary>
    [SkippableFact]
    public void Every_parameter_BC_writes_is_reproduced_exactly_from_the_symbol_file()
    {
        int joinedMethods = 0, exactParameters = 0, refusedLists = 0;
        var disagreements = new List<string>();

        foreach (var (bundle, app) in Bundles())
        {
            var derived = DerivedMethods(app);
            foreach (var obj in bundle.Objects.Where(o => o.Kind == "CodeUnit"))
                foreach (var emitted in EmittedMethods(Path.Combine(bundle.Directory, obj.File)))
                {
                    if (!derived.TryGetValue((obj.Id, emitted.Id), out var symbol)) continue;
                    joinedMethods++;

                    if (symbol.Parameters is null)
                    {
                        // A refusal is a legitimate answer and NOT a disagreement: the whole
                        // element is withdrawn, so nothing wrong is stated. It is counted so
                        // this test can report how much of BC's output the derivation declines.
                        refusedLists++;
                        continue;
                    }

                    var want = emitted.Parameters ?? Array.Empty<EmittedParameter>();
                    if (want.Count != symbol.Parameters.Count)
                    {
                        if (disagreements.Count < 10)
                            disagreements.Add(
                                $"{bundle.AppName} codeunit {obj.Id} '{emitted.Name}': BC writes " +
                                $"{want.Count} parameter(s), the derivation states " +
                                $"{symbol.Parameters.Count}");
                        continue;
                    }

                    for (var i = 0; i < want.Count; i++)
                    {
                        var b = want[i];
                        var d = symbol.Parameters[i];
                        if (b.Name == d.Name
                            && b.RuntimeType == d.RuntimeType
                            && b.RuntimeAttributes == d.RuntimeAttributes
                            && b.IsVar == d.IsVar
                            && !b.IsArray
                            && b.Length == d.Length)
                        {
                            exactParameters++;
                            continue;
                        }
                        if (disagreements.Count < 10)
                            disagreements.Add(
                                $"{bundle.AppName} codeunit {obj.Id} '{emitted.Name}' parameter {i}: " +
                                $"BC writes Name={b.Name} RuntimeType={b.RuntimeType} " +
                                $"RuntimeAttributes='{b.RuntimeAttributes}' IsVar={b.IsVar} " +
                                $"IsArray={b.IsArray} Length={b.Length?.ToString() ?? "(absent)"}; " +
                                $"the derivation states Name={d.Name} RuntimeType={d.RuntimeType} " +
                                $"RuntimeAttributes='{d.RuntimeAttributes}' IsVar={d.IsVar} " +
                                $"Length={d.Length?.ToString() ?? "(absent)"}");
                    }
                }
        }

        Assert.True(disagreements.Count == 0,
            $"the derivation disagrees with BC's own emitter on {disagreements.Count} parameter(s). " +
            "Every one is a <Parameter> element the runner would publish naming or typing " +
            "something other than what BC states, which is the manufactured agreement #4084 was " +
            "filed to prevent — do NOT widen a mapping to make this pass without re-measuring " +
            "the whole population:" + Environment.NewLine
            + string.Join(Environment.NewLine, disagreements));

        Assert.True(joinedMethods > 0 && exactParameters > 0,
            $"joined {joinedMethods} method(s) and checked {exactParameters} parameter(s); this " +
            "test has stopped measuring the join it is about, so its silence means nothing. " +
            $"{refusedLists} method(s) had their list refused.");
    }

    /// <summary>
    /// The casing rule is a pure first-character lowercase, pinned on the three parameters in the
    /// shipped apps whose AL identifier has two or more leading capitals — the only ones that
    /// separate it from the word-aware rule the eye supplies.
    ///
    /// <para>Kept separate from the population test above deliberately. That test would go red on
    /// a word-aware rule too, but it would go red on 2 of 1,308 — a ratio that reads as noise and
    /// invites a tolerance. This one names the shape and fails with the alternative spelled out,
    /// so the next editor sees what the disagreement MEANS (verify-execution-not-the-tick.md: a
    /// count is not a diagnosis).</para>
    /// </summary>
    [SkippableFact]
    public void The_casing_rule_is_a_pure_first_character_lowercase_not_a_word_aware_one()
    {
        var discriminating = new List<(string Al, string Bc)>();
        var wordAwareWouldDiffer = 0;

        foreach (var (bundle, app) in Bundles())
        {
            var alNames = AlParameterNamesByMethod(app);
            var derived = DerivedMethods(app);
            foreach (var obj in bundle.Objects.Where(o => o.Kind == "CodeUnit"))
                foreach (var emitted in EmittedMethods(Path.Combine(bundle.Directory, obj.File)))
                {
                    if (!alNames.TryGetValue((obj.Id, emitted.Id), out var al)) continue;
                    if (!derived.TryGetValue((obj.Id, emitted.Id), out var symbol)) continue;
                    if (symbol.Parameters is null) continue;
                    var want = emitted.Parameters ?? Array.Empty<EmittedParameter>();
                    if (want.Count != al.Count || want.Count != symbol.Parameters.Count) continue;

                    for (var i = 0; i < al.Count; i++)
                    {
                        if (al[i].Length < 2 || !char.IsUpper(al[i][0]) || !char.IsUpper(al[i][1]))
                            continue;
                        discriminating.Add((al[i], want[i].Name));
                        if (WordAwareCamelCase(al[i]) != want[i].Name) wordAwareWouldDiffer++;
                        // The rule under test answers BC's value on exactly this shape.
                        Assert.Equal(want[i].Name, symbol.Parameters[i].Name);
                    }
                }
        }

        // Non-vacuity, and the discrimination itself: the shape has to OCCUR, and the rival rule
        // has to be WRONG on some of it. A population with no multi-capital identifier would let
        // both rules pass, and reporting that as a pin would be the false green tdd.md describes.
        Assert.True(discriminating.Count > 0,
            "no parameter with two or more leading capitals was found, so this test cannot tell " +
            "a first-character rule from a word-aware one and its pass asserts nothing. The "
            + "shipped apps had three when #4084 was measured (AFSOperationResponse, AADObjectID, "
            + "IDataArchiveProvider).");

        Assert.True(wordAwareWouldDiffer > 0,
            $"{discriminating.Count} parameter(s) with two or more leading capitals were found and " +
            "a word-aware camelCase rule agrees with BC on ALL of them, so this test no longer " +
            "discriminates the two rules. Re-measure before trusting either:" + Environment.NewLine
            + string.Join(Environment.NewLine,
                discriminating.Select(p => $"  AL={p.Al} BC={p.Bc} word-aware={WordAwareCamelCase(p.Al)}")));
    }

    /// <summary>
    /// The rival rule, implemented here so the test can show what it WOULD have written rather
    /// than assert that it differs. Lowercases the whole leading run of capitals, leaving the last
    /// one when a lowercase letter follows it — which is what "camelCase this identifier" means to
    /// a reader and to most naming helpers.
    /// </summary>
    private static string WordAwareCamelCase(string name)
    {
        var run = 0;
        while (run < name.Length && char.IsUpper(name[run])) run++;
        if (run == 0) return name;
        if (run == name.Length) return name.ToLowerInvariant();
        if (run == 1) return char.ToLowerInvariant(name[0]) + name.Substring(1);
        return name.Substring(0, run - 1).ToLowerInvariant() + name.Substring(run - 1);
    }

    /// <summary>
    /// The AL parameter names SymbolReference.json states, per method — read straight out of the
    /// package rather than through BcAppSymbolCache, so the casing test compares BC's output
    /// against the AL SOURCE spelling and not against the runner's own transform of it.
    /// </summary>
    private static IReadOnlyDictionary<(int Codeunit, int Method), IReadOnlyList<string>>
        AlParameterNamesByMethod(string appPath)
    {
        var found = new Dictionary<(int, int), IReadOnlyList<string>>();
        using var doc = JsonDocument.Parse(SymbolReferenceBytes(appPath));
        Visit(doc.RootElement);
        return found;

        void Visit(JsonElement container)
        {
            if (container.TryGetProperty("Codeunits", out var codeunits)
                && codeunits.ValueKind == JsonValueKind.Array)
                foreach (var codeunit in codeunits.EnumerateArray())
                {
                    if (!codeunit.TryGetProperty("Id", out var cid) || !cid.TryGetInt32(out var objectId))
                        continue;
                    if (!codeunit.TryGetProperty("Methods", out var methods)
                        || methods.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var method in methods.EnumerateArray())
                    {
                        if (!method.TryGetProperty("Id", out var mid) || !mid.TryGetInt32(out var methodId))
                            continue;
                        var names = new List<string>();
                        if (method.TryGetProperty("Parameters", out var parameters)
                            && parameters.ValueKind == JsonValueKind.Array)
                            foreach (var parameter in parameters.EnumerateArray())
                                if (parameter.TryGetProperty("Name", out var n)
                                    && n.GetString() is { Length: > 0 } text)
                                    names.Add(text);
                        found[(objectId, methodId)] = names;
                    }
                }

            if (container.TryGetProperty("Namespaces", out var namespaces)
                && namespaces.ValueKind == JsonValueKind.Array)
                foreach (var child in namespaces.EnumerateArray())
                    Visit(child);
        }
    }

    /// <summary>
    /// The <c>SymbolReference.json</c> bytes inside a shipped <c>.app</c>. A Microsoft R2R package
    /// wraps a second <c>.app</c>, so the entry is one level down, and both levels carry the
    /// 40-byte NAVX header before the zip. Microsoft writes the file with a UTF-8 BOM, which
    /// <c>JsonDocument.Parse</c> rejects rather than skipping.
    ///
    /// <para>Read WITHOUT going through BcAppSymbolCache on purpose — see
    /// <see cref="AlParameterNamesByMethod"/>. The same routine, for the same reason, as
    /// <c>CodeunitMethodTableDerivabilityTests.SymbolReferenceBytes</c>.</para>
    /// </summary>
    private static byte[] SymbolReferenceBytes(string appPath)
    {
        const int NavxHeaderLength = 40;
        var outer = File.ReadAllBytes(appPath);
        using var zip = new System.IO.Compression.ZipArchive(
            new MemoryStream(outer, NavxHeaderLength, outer.Length - NavxHeaderLength));

        var direct = zip.GetEntry("SymbolReference.json");
        if (direct is not null) return StripUtf8Bom(ReadAll(direct));

        var nested = zip.Entries.FirstOrDefault(
            e => e.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"'{Path.GetFileName(appPath)}' carries neither SymbolReference.json nor a nested .app.");

        var inner = ReadAll(nested);
        using var innerZip = new System.IO.Compression.ZipArchive(
            new MemoryStream(inner, NavxHeaderLength, inner.Length - NavxHeaderLength));
        var entry = innerZip.GetEntry("SymbolReference.json")
            ?? throw new InvalidOperationException(
                $"the nested package inside '{Path.GetFileName(appPath)}' carries no SymbolReference.json.");
        return StripUtf8Bom(ReadAll(entry));

        static byte[] ReadAll(System.IO.Compression.ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        static byte[] StripUtf8Bom(byte[] bytes)
            => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                ? bytes[3..]
                : bytes;
    }
}
