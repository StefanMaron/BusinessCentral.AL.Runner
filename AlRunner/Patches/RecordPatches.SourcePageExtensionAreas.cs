// RecordPatches.SourcePageExtensionAreas — the ApplicationArea of every field control, part and
// action a pageextension compiled in this bundle adds to a page, and of every base control or
// action its modify() sets one on, read from the extension's own MetadataRuntimeDeltas document
// (#4866, #4871).
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
    /// <param name="AddedParts">Every part under a <c>ControlAdd</c> that BC's
    /// <c>RemoveControl</c> tests for area: an <c>InfopartSystemDefinition</c>, and an
    /// <c>InfopartPageDefinition</c> whose <c>Visible</c> is not literally false.</param>
    /// <param name="AddedActions">Every <c>ActionDefinition</c>, <c>CustomActionDefinition</c>
    /// and <c>FileUploadActionDefinition</c> under an <c>ActionAdd</c>, including inside an
    /// added group.</param>
    /// <param name="ActionAreaChanges"><c>ActionChange TargetID → ApplicationArea</c>.</param>
    /// <param name="AddedActionRefs">Every <c>ActionRefDefinition</c> under an <c>ActionAdd</c>,
    /// with its own area (null when it states none) and its target action's id.</param>
    internal sealed record SourcePageExtensionAreaSet(
        IReadOnlyList<(int Id, string? ApplicationArea)> AddedFieldControls,
        IReadOnlyDictionary<int, string> AreaChanges,
        IReadOnlyList<(int Id, string? ApplicationArea)> AddedParts,
        IReadOnlyList<(int Id, string? ApplicationArea)> AddedActions,
        IReadOnlyDictionary<int, string> ActionAreaChanges,
        IReadOnlyList<(int Id, string? ApplicationArea, int TargetId)> AddedActionRefs);

    private static readonly SourcePageExtensionAreaSet NoSourcePageExtensionAreas =
        new(Array.Empty<(int, string?)>(), new Dictionary<int, string>(),
            Array.Empty<(int, string?)>(), Array.Empty<(int, string?)>(), new Dictionary<int, string>(),
            Array.Empty<(int, string?, int)>());

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
        var parts = new List<(int, string?)>();
        var actions = new List<(int, string?)>();
        var actionRefs = new List<(int, string?, int)>();
        var changes = new Dictionary<int, string>();
        var changedBy = new Dictionary<int, int>();
        var actionChanges = new Dictionary<int, string>();
        var actionChangedBy = new Dictionary<int, int>();
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
                    CollectAddedControlAreas(e, added, parts);
                else if (e.Name == "ActionAdd")
                    CollectAddedActionAreas(e, actions, actionRefs);
                else if (e.Name == "ControlChange" && e.HasAttribute("ApplicationArea"))
                    RecordAreaChange(pageId, "control", extId, e, changes, changedBy);
                else if (e.Name == "ActionChange" && e.HasAttribute("ApplicationArea"))
                    RecordAreaChange(pageId, "action", extId, e, actionChanges, actionChangedBy);
            }
        }
        return new SourcePageExtensionAreaSet(added, changes, parts, actions, actionChanges, actionRefs);
    }

    private static void RecordAreaChange(int pageId, string kind, int extId, XmlElement change,
        Dictionary<int, string> changes, Dictionary<int, int> changedBy)
    {
        var target = ReadBcAttrInt(change, "TargetID");
        var area = change.GetAttribute("ApplicationArea");
        if (changes.TryGetValue(target, out var earlier) && !string.Equals(earlier, area, StringComparison.Ordinal))
            throw TestPageShapeGap.ControlProperty(
                $"TestPage ApplicationArea on page {pageId} {kind} {target}",
                $"pageextensions {changedBy[target]} and {extId} both modify it, to '{earlier}' and "
                + $"'{area}', and which one BC applies has not been measured (#4866)");
        changes[target] = area;
        changedBy[target] = extId;
    }

    private static string? AreaAttribute(XmlElement e) =>
        e.HasAttribute("ApplicationArea") ? e.GetAttribute("ApplicationArea") : null;

    // A group's own area does not reach what is inside it (MetadataProvider.RemoveControl tests
    // a ControlDefinition or a part only), so each element answers for itself.
    private static void CollectAddedControlAreas(XmlElement parent, List<(int, string?)> fields, List<(int, string?)> parts)
    {
        foreach (XmlNode node in parent.ChildNodes)
        {
            if (node is not XmlElement e || e.Name != "Controls") continue;
            switch (e.GetAttribute("type", XsiNamespace))
            {
                case "ControlDefinition":
                    fields.Add((ReadBcAttrInt(e, "ID"), AreaAttribute(e)));
                    break;
                case "InfopartPageDefinition" when !BcPropertyIsFalse(e.GetAttribute("Visible")):
                case "InfopartSystemDefinition":
                    parts.Add((ReadBcAttrInt(e, "ID"), AreaAttribute(e)));
                    break;
            }
            CollectAddedControlAreas(e, fields, parts);
        }
    }

    private static void CollectAddedActionAreas(XmlElement parent, List<(int, string?)> into, List<(int, string?, int)> refs)
    {
        foreach (XmlNode node in parent.ChildNodes)
        {
            if (node is not XmlElement e || e.Name != "Actions") continue;
            switch (e.GetAttribute("type", XsiNamespace))
            {
                case "ActionDefinition":
                case "CustomActionDefinition":
                case "FileUploadActionDefinition":
                    into.Add((ReadBcAttrInt(e, "ID"), AreaAttribute(e)));
                    break;
                case "ActionRefDefinition":
                    refs.Add((ReadBcAttrInt(e, "ID"), AreaAttribute(e), ReadBcAttrInt(e, "TargetID")));
                    break;
            }
            CollectAddedActionAreas(e, into, refs);
        }
    }
}
