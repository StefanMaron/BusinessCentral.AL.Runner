// RecordPatches.SourcePageExtensionAreas — the ApplicationArea of every field control a
// pageextension compiled in this bundle adds to a page, and of every base control its
// modify() sets one on, read from the extension's own MetadataRuntimeDeltas document (#4866).
// The runner's MasterPage carries no extension delta, so BC's removal pass cannot see either.
// Consumer: ApplicationAreaControlRemoval; see
// docs/dependency-page-properties.md#field-control-application-area.
using System.Xml;
using AlRunner.Infrastructure;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <param name="AddedFieldControls">Every <c>ControlDefinition</c> under a <c>ControlAdd</c>,
    /// with its own <c>ApplicationArea</c> attribute or null: the emitter writes none for a
    /// control that states none, whatever the base page states at object level.</param>
    /// <param name="AreaChanges"><c>ControlChange TargetID → ApplicationArea</c>, for each
    /// <c>modify()</c> that states one. It replaces the target's area.</param>
    internal sealed record SourcePageExtensionAreaSet(
        IReadOnlyList<(int Id, string? ApplicationArea)> AddedFieldControls,
        IReadOnlyDictionary<int, string> AreaChanges);

    private static readonly SourcePageExtensionAreaSet NoSourcePageExtensionAreas =
        new(Array.Empty<(int, string?)>(), new Dictionary<int, string>());

    /// <summary>
    /// The areas the source-compiled pageextensions of <paramref name="pageId"/> contribute.
    /// Precompiled extensions are left to <see cref="DependencyFieldControlAreas"/>, which reads
    /// them from the symbol file. Two extensions setting one control to different areas refuse:
    /// which BC applies has not been measured (#4761's rule for the precompiled side).
    /// </summary>
    internal static SourcePageExtensionAreaSet SourcePageExtensionAreas(int pageId)
    {
        var extensionIds = GetPageExtensionIdsForPage(pageId).Where(_parsedPageExtensions.ContainsKey).ToList();
        if (extensionIds.Count == 0) return NoSourcePageExtensionAreas;

        var added = new List<(int, string?)>();
        var changes = new Dictionary<int, string>();
        var changedBy = new Dictionary<int, int>();
        foreach (var extId in extensionIds)
        {
            // Parsed but never captured is "could not measure", never "adds nothing".
            if (!AlObjectMetadataRegistry.TryGet(BcPageExtensionMetadataKind, extId, out var xml) || string.IsNullOrEmpty(xml))
                throw TestPageShapeGap.ControlProperty(
                    $"TestPage ApplicationArea on page {pageId}",
                    $"pageextension {extId} was compiled from source, but BC's emitted delta document for it "
                    + "is not in the metadata registry, so the areas of the controls it adds or modifies "
                    + "cannot be read (#4866)");
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            foreach (XmlNode node in doc.DocumentElement!.ChildNodes)
            {
                if (node is not XmlElement e) continue;
                if (e.Name == "ControlAdd")
                    CollectAddedFieldControlAreas(e, added);
                else if (e.Name == "ControlChange" && e.HasAttribute("ApplicationArea"))
                {
                    var target = ReadBcAttrInt(e, "TargetID");
                    var area = e.GetAttribute("ApplicationArea");
                    if (changes.TryGetValue(target, out var earlier) && !string.Equals(earlier, area, StringComparison.Ordinal))
                        throw TestPageShapeGap.ControlProperty(
                            $"TestPage ApplicationArea on page {pageId} control {target}",
                            $"pageextensions {changedBy[target]} and {extId} both modify it, to '{earlier}' and "
                            + $"'{area}', and which one BC applies has not been measured (#4866)");
                    changes[target] = area;
                    changedBy[target] = extId;
                }
            }
        }
        return new SourcePageExtensionAreaSet(added, changes);
    }

    // A group's own area does not reach the fields inside it (MetadataProvider.RemoveControl
    // tests a ControlDefinition only), so each field answers for itself.
    private static void CollectAddedFieldControlAreas(XmlElement parent, List<(int, string?)> into)
    {
        foreach (XmlNode node in parent.ChildNodes)
        {
            if (node is not XmlElement e || e.Name != "Controls") continue;
            var type = e.GetAttribute("type", "http://www.w3.org/2001/XMLSchema-instance");
            if (type == "ControlDefinition")
                into.Add((ReadBcAttrInt(e, "ID"), e.HasAttribute("ApplicationArea") ? e.GetAttribute("ApplicationArea") : null));
            CollectAddedFieldControlAreas(e, into);
        }
    }
}
