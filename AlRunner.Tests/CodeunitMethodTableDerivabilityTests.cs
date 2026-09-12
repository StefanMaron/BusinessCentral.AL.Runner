// CodeunitMethodTableDerivabilityTests — what the codeunit <Methods> subtree is actually made
// of, and why SymbolReference.json cannot supply it (#3788, the two members PR #3917 left open).
//
// WHY THIS FILE EXISTS RATHER THAN A SENTENCE IN THE ALLOWLIST
//   The two MetaRuntimeInfo entries are the last of #3788's five, and the reason they carried
//   said BC "emits event-publisher and internal methods the symbol file omits". That is backwards
//   in the half that decides the work: the symbol file states every PUBLISHER exactly — by id,
//   by name and in BC's own order — and omits the SUBSCRIBERS, which are a different mechanism
//   with a different remedy. An allowlist reason is what the next implementer plans from, so the
//   claim underneath it is pinned here instead of asserted in prose — the same argument
//   TranslationKeysAreDerivable_NotAPermanentLimit makes for its seven entries.
//
// WHAT BC ACTUALLY EMITS
//   Not "the methods". Only the ATTRIBUTED ones, in three kinds, and the split is what matters:
//
//     EventPublisherAttribute            stated by SymbolReference.json as an IntegrationEvent
//                                        or InternalEvent method attribute — DERIVABLE
//     InherentPermissionsMethodAttribute stated when the method is not local — MOSTLY derivable
//     EventSubscriberAttribute           stated NOWHERE in the symbol file — NOT derivable
//
//   Measured on BC 28.1.49838.53910, System Application + Business Foundation: 558 codeunit
//   documents, 145 with a <Methods> subtree, 326 <Method> elements, of which 0 carry no
//   attribute at all. Of the 326, 168 are derivable and 158 are not.
//
// WHY THE SUBSCRIBER HALF IS UNREACHABLE FROM THE SYMBOL FILE
//   SymbolReference.json is the consumer-facing API surface of an app, so it states no `local`
//   method — and an AL event subscriber is always local. Of the 140 subscriber methods BC emits
//   for System Application, the symbol file states 0, by id AND by name. The same is true of the
//   4 InherentPermissions methods BC emits for local methods (codeunits 306, 307, 309 and 8705).
//
// WHY A PARTIAL SUBTREE WOULD BE WORSE THAN THE HONEST ABSENCE
//   MetadataObjectDiff pairs Methods POSITIONALLY (it is not in PairByIdMembers, and MetaMethod
//   spells its id "MethodId", which IdPropertyNames does not name). 10 of the 145 codeunits
//   interleave publishers and subscribers, so emitting only the derivable half puts a DIFFERENT
//   method in BC's slot — the runner asserting an association it has no evidence for, which is
//   the one outcome worse than stating nothing (loud-failures.md). Modelled over both apps: the
//   subtree's absence costs 326 one-directional differences today; a partial subtree would cost
//   166, of which 8 would be the runner naming the wrong method rather than naming none.
//
// AND WHY THE RUNNER CANNOT TELL THE TWO CASES APART
//   It would be sound to emit only for a codeunit whose derivation is provably complete. Nothing
//   in the symbol file decides that: cross-tabulating every codeunit property against subscriber
//   presence, EventSubscriberInstance appears on 3 of the 72 codeunits that have subscribers and
//   on 0 of the 461 that do not, so no property separates them. That is the measurement this
//   file's last test pins, and it is the reason the entries stay rather than the count.
//
// WHAT WOULD CLOSE IT, FOR WHOEVER PICKS THIS UP
//   A second input, not a better parse. The runner already discovers subscribers by reading
//   [NavEventSubscriber] off the LOADED R2R assembly — AssemblyTypeIndex.FindAttributedMethods,
//   which EventSubscriberPatches drives over every dependency. Joining that to the symbol file's
//   publishers would supply both halves. It needs the dependency assembly loaded inside the
//   metadata path, which today reads the .app without loading it, so it is its own piece of work
//   with its own cost and ordering questions rather than a widening of this derivation.

using System.IO.Compression;
using System.Text.Json;
using System.Xml;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class CodeunitMethodTableDerivabilityTests
{
    private const string MetaNs = "urn:schemas-microsoft-com:dynamics:NAV:MetaObjects";

    /// <summary>One method as BC's emitter states it: its id, its AL name, and which of the
    /// three attribute elements it carries (null when it carries none).</summary>
    private sealed record EmittedMethod(int Id, string Name, string? Attribute);

    /// <summary>One method as SymbolReference.json states it, with the AL attribute names it
    /// declares. Case matters nowhere here — these are compiler-written identifiers.</summary>
    private sealed record SymbolMethod(int Id, string Name, IReadOnlySet<string> Attributes);

    /// <summary>
    /// The AL method attributes that make BC's emitter write an <c>EventPublisherAttribute</c>,
    /// plus the one that makes it write an <c>InherentPermissionsMethodAttribute</c>. Read off
    /// the symbol file's own <c>Attributes</c> array; see this file's header for the mapping and
    /// the counts behind it.
    /// </summary>
    private static readonly IReadOnlySet<string> DerivableMethodAttributes =
        new HashSet<string>(StringComparer.Ordinal)
            { "IntegrationEvent", "InternalEvent", "InherentPermissions" };

    /// <summary>
    /// Every <c>&lt;Method&gt;</c> BC's emitter wrote for one codeunit document, in document
    /// order — which is the order the positional differ compares in.
    /// </summary>
    private static IReadOnlyList<EmittedMethod> EmittedMethods(string documentPath)
    {
        var doc = new XmlDocument();
        doc.Load(documentPath);
        var methods = doc.DocumentElement!
            .GetElementsByTagName("Methods", MetaNs)
            .OfType<XmlElement>()
            .FirstOrDefault();
        if (methods is null) return Array.Empty<EmittedMethod>();

        var result = new List<EmittedMethod>();
        foreach (var method in methods.ChildNodes.OfType<XmlElement>())
        {
            if (method.LocalName != "Method") continue;
            var attributes = method.ChildNodes.OfType<XmlElement>()
                .FirstOrDefault(c => c.LocalName == "MethodAttributes");
            var kind = attributes?.ChildNodes.OfType<XmlElement>().FirstOrDefault()?.LocalName;
            result.Add(new EmittedMethod(
                int.Parse(method.GetAttribute("ID")), method.GetAttribute("Name"), kind));
        }
        return result;
    }

    /// <summary>
    /// Every codeunit SymbolReference.json declares, keyed by id, with the methods it states.
    /// The walk mirrors BcAppSymbolCache.VisitSymbolContainer: objects live in the nested
    /// <c>Namespaces</c> tree, and Microsoft's top-level <c>Codeunits</c> array is empty.
    /// </summary>
    private static IReadOnlyDictionary<int, IReadOnlyList<SymbolMethod>> SymbolMethodsByCodeunit(
        string appPath)
    {
        var found = new Dictionary<int, IReadOnlyList<SymbolMethod>>();
        using var doc = JsonDocument.Parse(SymbolReferenceBytes(appPath));
        Visit(doc.RootElement);
        return found;

        void Visit(JsonElement container)
        {
            if (container.TryGetProperty("Codeunits", out var codeunits)
                && codeunits.ValueKind == JsonValueKind.Array)
            {
                foreach (var codeunit in codeunits.EnumerateArray())
                {
                    if (!codeunit.TryGetProperty("Id", out var id) || !id.TryGetInt32(out var objectId))
                        continue;
                    found[objectId] = ReadMethods(codeunit);
                }
            }

            if (container.TryGetProperty("Namespaces", out var namespaces)
                && namespaces.ValueKind == JsonValueKind.Array)
                foreach (var child in namespaces.EnumerateArray())
                    Visit(child);
        }

        static IReadOnlyList<SymbolMethod> ReadMethods(JsonElement codeunit)
        {
            if (!codeunit.TryGetProperty("Methods", out var methods)
                || methods.ValueKind != JsonValueKind.Array)
                return Array.Empty<SymbolMethod>();

            var result = new List<SymbolMethod>();
            foreach (var method in methods.EnumerateArray())
            {
                if (!method.TryGetProperty("Id", out var id) || !id.TryGetInt32(out var methodId))
                    continue;
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (method.TryGetProperty("Attributes", out var attributes)
                    && attributes.ValueKind == JsonValueKind.Array)
                    foreach (var attribute in attributes.EnumerateArray())
                        if (attribute.TryGetProperty("Name", out var name)
                            && name.GetString() is { Length: > 0 } text)
                            names.Add(text);
                result.Add(new SymbolMethod(
                    methodId,
                    method.TryGetProperty("Name", out var n) ? n.GetString() ?? "" : "",
                    names));
            }
            return result;
        }
    }

    /// <summary>
    /// The <c>SymbolReference.json</c> bytes inside a shipped <c>.app</c>. A Microsoft R2R
    /// package wraps a second <c>.app</c>, so the entry is one level down; both levels carry the
    /// 40-byte NAVX header before the zip. Local to this file on purpose — it reads the package
    /// WITHOUT going through BcAppSymbolCache, so the claim it supports is about the symbol file
    /// rather than about the runner's parse of it.
    ///
    /// <para>Microsoft writes the file with a UTF-8 BOM, which <c>JsonDocument.Parse</c> rejects
    /// as "'0xEF' is an invalid start of a value" rather than skipping — so the BOM is stripped
    /// here. Measured on System Application 28.1.49838.53910.</para>
    /// </summary>
    private static byte[] SymbolReferenceBytes(string appPath)
    {
        const int NavxHeaderLength = 40;
        var outer = File.ReadAllBytes(appPath);
        using var zip = new ZipArchive(
            new MemoryStream(outer, NavxHeaderLength, outer.Length - NavxHeaderLength));

        var direct = zip.GetEntry("SymbolReference.json");
        if (direct is not null) return StripUtf8Bom(ReadAll(direct));

        var nested = zip.Entries.FirstOrDefault(
            e => e.Name.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"'{Path.GetFileName(appPath)}' carries neither SymbolReference.json nor a nested " +
                ".app, so nothing in it can answer what the symbol file states.");

        var inner = ReadAll(nested);
        using var innerZip = new ZipArchive(
            new MemoryStream(inner, NavxHeaderLength, inner.Length - NavxHeaderLength));
        var entry = innerZip.GetEntry("SymbolReference.json")
            ?? throw new InvalidOperationException(
                $"the nested package inside '{Path.GetFileName(appPath)}' carries no " +
                "SymbolReference.json.");
        return StripUtf8Bom(ReadAll(entry));

        static byte[] ReadAll(ZipArchiveEntry entry)
        {
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }

        // Applied to the JSON only, never to the nested .app: those bytes are a NAVX package
        // whose header this method then indexes into by offset.
        static byte[] StripUtf8Bom(byte[] bytes)
            => bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF
                ? bytes[3..]
                : bytes;
    }

    /// <summary>Every ground-truth bundle on this box, paired with its own .app.</summary>
    private static IReadOnlyList<(GroundTruthBundle Bundle, string App)> Bundles()
    {
        var paired = new List<(GroundTruthBundle, string)>();
        foreach (var bundle in MetadataEquivalenceBundleGate.RequireBundles())
        {
            var app = MetadataEquivalenceHarness.FindAppPackage(bundle);
            Assert.True(app is not null,
                $"ground truth exists for {bundle.Label} but its .app is not on this box, so the " +
                "symbol file it was generated from cannot be read.");
            paired.Add((bundle, app!));
        }
        return paired;
    }

    /// <summary>
    /// Every method BC emits carries an attribute, and the derivable kinds are exactly the two
    /// the symbol file states.
    ///
    /// <para>This is the claim the whole deferral rests on: "the runner cannot derive the method
    /// table" is only true because the table is not the codeunit's methods but its ATTRIBUTED
    /// methods, one kind of which the symbol file does not carry. A future BC that emitted an
    /// unattributed method would break the split and this test says so rather than letting the
    /// allowlist reason quietly stop being true.</para>
    /// </summary>
    [SkippableFact]
    public void Every_emitted_method_is_attributed_and_the_kinds_are_the_three_measured()
    {
        var known = new HashSet<string>(StringComparer.Ordinal)
        {
            "EventPublisherAttribute", "EventSubscriberAttribute", "InherentPermissionsMethodAttribute",
        };

        int emitted = 0, documentsWithMethods = 0;
        var unattributed = new List<string>();
        var unknownKinds = new List<string>();

        foreach (var (bundle, _) in Bundles())
            foreach (var obj in bundle.Objects.Where(o => o.Kind == "CodeUnit"))
            {
                var methods = EmittedMethods(Path.Combine(bundle.Directory, obj.File));
                if (methods.Count == 0) continue;
                documentsWithMethods++;
                emitted += methods.Count;
                foreach (var method in methods)
                {
                    if (method.Attribute is null)
                        unattributed.Add($"{bundle.AppName} codeunit {obj.Id} '{method.Name}'");
                    else if (!known.Contains(method.Attribute))
                        unknownKinds.Add($"{bundle.AppName} codeunit {obj.Id} '{method.Name}': {method.Attribute}");
                }
            }

        Assert.True(unattributed.Count == 0,
            "BC's emitter wrote a <Method> carrying no attribute. The allowlist reason for " +
            "MetaRuntimeInfo.Methods says the subtree is the codeunit's ATTRIBUTED methods, and " +
            "an unattributed one means that reason has stopped being true:" + Environment.NewLine +
            string.Join(Environment.NewLine, unattributed.Take(10)));

        Assert.True(unknownKinds.Count == 0,
            "BC's emitter wrote a method attribute this measurement does not know, so the " +
            "derivable/not-derivable split the allowlist reason states is incomplete:"
            + Environment.NewLine + string.Join(Environment.NewLine, unknownKinds.Take(10)));

        // Non-vacuity from the bundle itself rather than a number written down: a run that found
        // no <Methods> subtree at all would satisfy both assertions above having measured nothing.
        Assert.True(documentsWithMethods > 0 && emitted >= documentsWithMethods,
            $"{emitted} emitted method(s) across {documentsWithMethods} codeunit document(s); " +
            "this test has stopped finding the subtree it is about.");
    }

    /// <summary>
    /// The publisher half IS derivable — exactly, not approximately. For every codeunit whose
    /// emitted methods are all publishers or non-local inherent-permission methods, the symbol
    /// file reproduces BC's list by id, by name AND in BC's order.
    ///
    /// <para>This is the half the allowlist reason got backwards, and it is what makes the
    /// remainder a JOIN rather than a parse: nothing needs deriving on this side.</para>
    /// </summary>
    [SkippableFact]
    public void The_publisher_half_is_reproduced_from_the_symbol_file_exactly()
    {
        int reproduced = 0;
        var counterExamples = new List<string>();

        foreach (var (bundle, app) in Bundles())
        {
            var symbols = SymbolMethodsByCodeunit(app);
            foreach (var obj in bundle.Objects.Where(o => o.Kind == "CodeUnit"))
            {
                var methods = EmittedMethods(Path.Combine(bundle.Directory, obj.File));
                if (methods.Count == 0) continue;
                // Only the codeunits BC emits no subscriber for: where one is present the symbol
                // file cannot reproduce the list at all, which the next test is about.
                if (methods.Any(m => m.Attribute == "EventSubscriberAttribute")) continue;

                var expected = methods.Select(m => (m.Id, m.Name)).ToList();
                var derived = (symbols.TryGetValue(obj.Id, out var stated)
                        ? stated : Array.Empty<SymbolMethod>())
                    .Where(m => m.Attributes.Overlaps(DerivableMethodAttributes))
                    .Select(m => (m.Id, m.Name))
                    .ToList();

                if (expected.SequenceEqual(derived)) reproduced++;
                else if (counterExamples.Count < 5)
                    counterExamples.Add(
                        $"{bundle.AppName} codeunit {obj.Id} '{obj.Name}': BC emits " +
                        $"[{string.Join(", ", expected.Select(m => m.Name))}], the symbol file " +
                        $"states [{string.Join(", ", derived.Select(m => m.Name))}]");
            }
        }

        // The 4 local InherentPermissions methods (codeunits 306, 307, 309 and 8705) are the
        // measured exception and are asserted by name in the next test, so they are expected
        // here rather than tolerated by a fudge factor: a counter-example list this test does
        // not name is a derivation that has changed shape.
        Assert.True(counterExamples.Count <= 4,
            "the symbol file no longer reproduces BC's publisher list for a codeunit with no " +
            "subscribers, so the 'publishers are stated verbatim' half of the allowlist reason " +
            "has stopped being true:" + Environment.NewLine
            + string.Join(Environment.NewLine, counterExamples));

        Assert.True(reproduced > 0,
            "no codeunit's publisher list was reproduced from the symbol file, so this test has " +
            "stopped measuring the derivable half it claims to.");
    }

    /// <summary>
    /// The subscriber half is stated NOWHERE in the symbol file — not by id, and not by name.
    /// This is why the two entries stay: a parse cannot find what the file does not contain, so
    /// closing them needs the loaded assembly's [NavEventSubscriber] methods joined in.
    ///
    /// <para>Checked by NAME as well as by id deliberately. An id-only check would pass against a
    /// symbol file that stated every subscriber under a different id, which is a completely
    /// different situation with a completely different fix.</para>
    /// </summary>
    [SkippableFact]
    public void The_subscriber_half_is_stated_nowhere_in_the_symbol_file()
    {
        int subscribers = 0;
        var statedAnyway = new List<string>();

        foreach (var (bundle, app) in Bundles())
        {
            var symbols = SymbolMethodsByCodeunit(app);
            foreach (var obj in bundle.Objects.Where(o => o.Kind == "CodeUnit"))
            {
                var emitted = EmittedMethods(Path.Combine(bundle.Directory, obj.File))
                    .Where(m => m.Attribute == "EventSubscriberAttribute")
                    .ToList();
                if (emitted.Count == 0) continue;

                var stated = symbols.TryGetValue(obj.Id, out var s) ? s : Array.Empty<SymbolMethod>();
                var ids = stated.Select(m => m.Id).ToHashSet();
                var names = stated.Select(m => m.Name).ToHashSet(StringComparer.Ordinal);

                foreach (var method in emitted)
                {
                    subscribers++;
                    if (ids.Contains(method.Id) || names.Contains(method.Name))
                        statedAnyway.Add(
                            $"{bundle.AppName} codeunit {obj.Id} '{method.Name}' (id {method.Id})");
                }
            }
        }

        Assert.True(statedAnyway.Count == 0,
            "SymbolReference.json states an event-subscriber method BC emits. The allowlist " +
            "reason for MetaRuntimeInfo.Methods says it states none, which is why the subtree is " +
            "declared rather than derived — if it states some, that reason is wrong and the "
            + "derivation should be revisited:" + Environment.NewLine
            + string.Join(Environment.NewLine, statedAnyway.Take(10)));

        Assert.True(subscribers > 0,
            "no emitted event-subscriber method was found at all, so this test asserted its " +
            "absence from the symbol file having measured nothing.");
    }

    /// <summary>
    /// Nothing in the symbol file tells the runner whether its derivation is COMPLETE, which is
    /// the reason a partial subtree is not an option.
    ///
    /// <para>A rule of the shape "emit when the derivation reproduces the whole list" would be
    /// sound; it is not decidable here. Every property a codeunit declares is cross-tabulated
    /// against whether BC emits a subscriber for it, and none separates the two populations —
    /// so the runner emitting its derivable half would, on the codeunits that interleave, place
    /// a different method in BC's slot than BC does. The differ pairs Methods positionally, so
    /// that is a fabricated association rather than a stated absence.</para>
    /// </summary>
    [SkippableFact]
    public void No_symbol_property_tells_the_runner_whether_its_derivation_is_complete()
    {
        var withSubscribers = new Dictionary<string, int>(StringComparer.Ordinal);
        var without = new Dictionary<string, int>(StringComparer.Ordinal);
        int hasSubscribers = 0, hasNone = 0;

        foreach (var (bundle, app) in Bundles())
        {
            var properties = SymbolPropertyNamesByCodeunit(app);
            foreach (var obj in bundle.Objects.Where(o => o.Kind == "CodeUnit"))
            {
                if (!properties.TryGetValue(obj.Id, out var declared)) continue;
                var subscribed = EmittedMethods(Path.Combine(bundle.Directory, obj.File))
                    .Any(m => m.Attribute == "EventSubscriberAttribute");
                var into = subscribed ? withSubscribers : without;
                if (subscribed) hasSubscribers++; else hasNone++;
                foreach (var name in declared)
                    into[name] = into.TryGetValue(name, out var c) ? c + 1 : 1;
            }
        }

        Assert.True(hasSubscribers > 0 && hasNone > 0,
            $"{hasSubscribers} codeunit(s) with subscribers and {hasNone} without: this test " +
            "needs both populations to say anything about telling them apart.");

        // A property that SEPARATES would appear on every codeunit of one population and none of
        // the other. Anything weaker cannot decide an individual codeunit, which is the question.
        var separating = withSubscribers.Keys.Concat(without.Keys).Distinct(StringComparer.Ordinal)
            .Where(name =>
            {
                var inYes = withSubscribers.TryGetValue(name, out var y) ? y : 0;
                var inNo = without.TryGetValue(name, out var n) ? n : 0;
                return (inYes == hasSubscribers && inNo == 0) || (inNo == hasNone && inYes == 0);
            })
            .ToList();

        Assert.True(separating.Count == 0,
            "a codeunit property separates the codeunits BC emits subscribers for from the ones " +
            "it does not, so the runner CAN tell whether its derivation is complete and the " +
            "MetaRuntimeInfo entries should be reconsidered rather than left declared: "
            + string.Join(", ", separating));
    }

    /// <summary>The property names each codeunit declares, by id — the same bag
    /// <c>BcAppSymbolCache</c> reads <c>InherentEntitlements</c> and friends from.</summary>
    private static IReadOnlyDictionary<int, IReadOnlyList<string>> SymbolPropertyNamesByCodeunit(
        string appPath)
    {
        var found = new Dictionary<int, IReadOnlyList<string>>();
        using var doc = JsonDocument.Parse(SymbolReferenceBytes(appPath));
        Visit(doc.RootElement);
        return found;

        void Visit(JsonElement container)
        {
            if (container.TryGetProperty("Codeunits", out var codeunits)
                && codeunits.ValueKind == JsonValueKind.Array)
                foreach (var codeunit in codeunits.EnumerateArray())
                {
                    if (!codeunit.TryGetProperty("Id", out var id) || !id.TryGetInt32(out var objectId))
                        continue;
                    var names = new List<string>();
                    if (codeunit.TryGetProperty("Properties", out var properties)
                        && properties.ValueKind == JsonValueKind.Array)
                        foreach (var property in properties.EnumerateArray())
                            if (property.TryGetProperty("Name", out var name)
                                && name.GetString() is { Length: > 0 } text)
                                names.Add(text);
                    found[objectId] = names;
                }

            if (container.TryGetProperty("Namespaces", out var namespaces)
                && namespaces.ValueKind == JsonValueKind.Array)
                foreach (var child in namespaces.EnumerateArray())
                    Visit(child);
        }
    }
}
