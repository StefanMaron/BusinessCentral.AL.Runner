// MetadataDocumentPresenceDiff — the harness's THIRD state, for issue #4357.
//
// MetadataObjectDiff compares two PARSED objects, where an attribute a document omits and an
// attribute a document states with the parse default are the same value. So a member the runner
// never writes is invisible on every object whose BC-written value happens to equal that
// default. Measured on BC 28.1.49838.53910 (Ncl 49b11d9b, Types c91ede8f): BC writes
// PageProperties/@AnalysisModeEnabled on 94 of 235 pages and the runner writes it on none, and
// the object diff reports ONE of them — page 8350, the only page where BC says "0".
//
// This is not "report defaults". Two things keep it to the set the object diff cannot see: only
// elements BOTH sides build are considered, and only attributes whose removal leaves BC's own
// object unchanged are reported. An omission the value comparison already reports is therefore
// not reported again. The measured populations are in
// docs/metadata-equivalence.md#unobservable-omissions, where they can be re-run.
//
// It is a third state rather than a difference (.claude/rules/guards-need-a-third-state.md):
// the two objects AGREE on the member. What is being reported is that the harness cannot tell
// whether they agree because the runner derived it or because it derived nothing.
//
// Nothing here is BC-specific in its types — it takes XmlDocument plus a parse delegate — so it
// is unit-testable with plain C# fixtures and no service tier, the same way MetadataObjectDiff
// is. See MetadataDocumentPresenceDiffTests.

using System.Xml;

namespace AlRunner.Metadata;

/// <summary>
/// An attribute one side's document states and the other's omits, at an element BOTH sides
/// build, where removing it from the stating side's document changes nothing in the parsed
/// object. The two derivations agree on the member and the agreement proves nothing.
/// </summary>
/// <param name="ObjectKey">The object being compared, e.g. <c>Page 8350</c>.</param>
/// <param name="ElementPath">Structural path to the element, e.g. <c>/Properties[0]</c>.</param>
/// <param name="Element">The element's local name, e.g. <c>Properties</c>.</param>
/// <param name="Attribute">The attribute's local name, e.g. <c>AnalysisModeEnabled</c>.</param>
/// <param name="Value">What the stating side wrote, e.g. <c>1</c>.</param>
/// <param name="StatedBy">
/// <see cref="MetadataDocumentPresenceDiff.ExpectedSide"/> when BC's document states it and the
/// runner's omits it, <see cref="MetadataDocumentPresenceDiff.ActualSide"/> the other way round.
/// Both directions are reported: an attribute the runner invents that BC does not state is the
/// same blind spot mirrored, and it is the direction a future writer-side fix introduces.
/// </param>
public sealed record MetadataUnobservableOmission(
    string ObjectKey,
    string ElementPath,
    string Element,
    string Attribute,
    string Value,
    string StatedBy)
{
    /// <summary>The <c>Element.Attribute</c> pair a declaration entry names.</summary>
    public string Signature => Element + "." + Attribute;

    public override string ToString()
        => $"{ObjectKey} {ElementPath}/@{Attribute}='{Value}' stated by {StatedBy} only, " +
           "and the parsed objects agree";
}

public static class MetadataDocumentPresenceDiff
{
    public const string ExpectedSide = "expected";
    public const string ActualSide = "actual";

    /// <summary>
    /// Attributes whose presence the object comparison cannot observe, in both directions.
    /// </summary>
    /// <param name="expectedDoc">BC's own emitted document.</param>
    /// <param name="actualDoc">The runner's rendered document, read by the SAME BC reader.</param>
    /// <param name="parse">
    /// BC's own reader. Required rather than optional, and a <c>null</c> return is treated as
    /// "could not measure" and reported — never as "no omission is observable", which is the
    /// success state wearing the third state's clothes.
    /// </param>
    public static IReadOnlyList<MetadataUnobservableOmission> Compare(
        XmlDocument expectedDoc,
        XmlDocument actualDoc,
        Func<XmlDocument, object?> parse,
        string objectKey,
        MetadataObjectDiffOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(expectedDoc);
        ArgumentNullException.ThrowIfNull(actualDoc);
        ArgumentNullException.ThrowIfNull(parse);

        var expectedElements = IndexElements(expectedDoc);
        var actualElements = IndexElements(actualDoc);

        var found = new List<MetadataUnobservableOmission>();
        Collect(expectedDoc, expectedElements, actualElements, parse, objectKey, options,
                ExpectedSide, found);
        Collect(actualDoc, actualElements, expectedElements, parse, objectKey, options,
                ActualSide, found);
        return found;
    }

    private static void Collect(
        XmlDocument statingDoc,
        IReadOnlyDictionary<string, XmlElement> statingElements,
        IReadOnlyDictionary<string, XmlElement> otherElements,
        Func<XmlDocument, object?> parse,
        string objectKey,
        MetadataObjectDiffOptions? options,
        string statedBy,
        List<MetadataUnobservableOmission> found)
    {
        // One probe read, ONLY to tell "this document is unreadable" from "this document has
        // no unobservable omission". The per-attribute baseline below is parsed again each
        // time, and that is not redundant — see ParseCold.
        if (parse(statingDoc) is null)
        {
            found.Add(new MetadataUnobservableOmission(
                objectKey, "", "<document>", "<unreadable>", "",
                statedBy + " — its own reader returned null, so no omission on this side was measured"));
            return;
        }

        foreach (var (path, element) in statingElements.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            // Only elements the OTHER side builds too. An element it does not build at all is
            // already reported by MetadataObjectDiff as a <presence> difference, and every
            // attribute on it would repeat that one finding once per attribute — which on a page
            // means every ordinary field control, none of which the runner builds.
            if (!otherElements.TryGetValue(path, out var counterpart)) continue;

            foreach (var attribute in element.Attributes.Cast<XmlAttribute>().ToArray())
            {
                if (IsNamespaceDeclaration(attribute)) continue;
                if (HasAttribute(counterpart, attribute)) continue;

                // BC's own reader decides, by being asked the same document twice. A name-based
                // join from the attribute to an object member would be a guess, and it would
                // guess in the direction that RESTORES the blind spot: a member whose name
                // coincidentally matches a reported difference reads as observable.
                // BOTH sides parsed fresh, for this one question. Reusing a single baseline
                // across strips makes the verdict depend on walk ORDER: MetadataObjectDiff
                // reads every readable member, which FORCES BC's lazily-built state, so a
                // baseline that earlier walks have warmed no longer matches a document just
                // parsed. Measured on BC 28.1.49838.53910 — MetaReport holds a LazyEx whose
                // IsValueCreated reads False on a cold object and True on a warmed one, and a
                // reused baseline reported Report 9810's PromotedActionCategoriesML as
                // observable on the strength of `LazyEx`1.IsValueCreated 'True' -> 'False'`,
                // a difference about this harness rather than about either derivation.
                // Deliberately NOT fixed by ignoring LazyEx: the next lazily-built member BC
                // adds would reintroduce it silently.
                var intact = parse(statingDoc);
                var strippedObject = parse(CloneWithout(statingDoc, path, attribute));
                if (intact is null || strippedObject is null)
                {
                    found.Add(new MetadataUnobservableOmission(
                        objectKey, path, element.LocalName, attribute.LocalName, attribute.Value,
                        statedBy + " — the reader returned null for the stripped document, so " +
                        "this attribute's observability was not measured"));
                    continue;
                }

                // The SAME options the value comparison used for this kind, so the two answers
                // are about one pairing rule. An earlier revision passed a capped copy here, to
                // stop the walk after the first difference since this question is yes/no and
                // the list is discarded. It was removed: measured on this box the cap was worth
                // about half a second of a twelve-second class, which is inside the run-to-run
                // spread, and the justification written into it had been a figure nobody took.
                if (MetadataObjectDiff.Compare(intact, strippedObject, objectKey, options).Count == 0)
                    found.Add(new MetadataUnobservableOmission(
                        objectKey, path, element.LocalName, attribute.LocalName,
                        attribute.Value, statedBy));
            }
        }
    }

    /// <summary>
    /// Every element in the document, keyed by a structural path built from local names and
    /// sibling ordinals — <c>/Properties[0]/Control[2]</c>. Local names, because BC's emitted
    /// documents carry a default namespace and the runner's rendering of the same document does
    /// not always repeat it; the pairing is about position and shape, not about the namespace.
    /// </summary>
    private static Dictionary<string, XmlElement> IndexElements(XmlDocument document)
    {
        var index = new Dictionary<string, XmlElement>(StringComparer.Ordinal);
        if (document.DocumentElement is { } root) Index(root, "", index);
        return index;
    }

    private static void Index(XmlElement element, string path, Dictionary<string, XmlElement> index)
    {
        index[path] = element;
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var child in element.ChildNodes.OfType<XmlElement>())
        {
            seen.TryGetValue(child.LocalName, out var ordinal);
            seen[child.LocalName] = ordinal + 1;
            Index(child, $"{path}/{child.LocalName}[{ordinal}]", index);
        }
    }

    private static bool IsNamespaceDeclaration(XmlAttribute attribute)
        => attribute.Prefix == "xmlns"
           || string.Equals(attribute.Name, "xmlns", StringComparison.Ordinal);

    /// <summary>
    /// Matched on LOCAL name, for the reason <see cref="IndexElements"/> gives: the two sides
    /// spell namespaces differently and an attribute matched on the qualified name would read
    /// as omitted on every element of every document.
    /// </summary>
    private static bool HasAttribute(XmlElement element, XmlAttribute wanted)
        => element.Attributes.Cast<XmlAttribute>().Any(
            a => string.Equals(a.LocalName, wanted.LocalName, StringComparison.Ordinal));

    private static XmlDocument CloneWithout(XmlDocument document, string path, XmlAttribute attribute)
    {
        var copy = new XmlDocument();
        copy.LoadXml(document.OuterXml);
        var index = IndexElements(copy);
        // The index is rebuilt from the copy rather than reused: XmlElement instances belong to
        // the document that owns them, so an index over the original names nodes this clone
        // does not have, and removing through it would leave the clone untouched — a mutation
        // that lands nowhere and reads as "the attribute was unobservable".
        var target = index[path];
        foreach (var candidate in target.Attributes.Cast<XmlAttribute>().ToArray())
            if (string.Equals(candidate.LocalName, attribute.LocalName, StringComparison.Ordinal)
                && string.Equals(candidate.NamespaceURI, attribute.NamespaceURI, StringComparison.Ordinal))
                target.Attributes.Remove(candidate);
        return copy;
    }
}
