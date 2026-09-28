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
        foreach (var (extId, xml) in SourcePageExtensionDeltaDocuments(pageId, extensionIds, $"TestPage ApplicationArea on page {pageId}"))
        {
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

    // Parsed but never captured is "could not measure", never "adds nothing".
    private static IEnumerable<(int ExtensionId, string Xml)> SourcePageExtensionDeltaDocuments(
        int pageId, IEnumerable<int> extensionIds, string api)
    {
        foreach (var extId in extensionIds)
        {
            if (!AlObjectMetadataRegistry.TryGet(BcPageExtensionMetadataKind, extId, out var xml) || string.IsNullOrEmpty(xml))
                throw TestPageShapeGap.ControlProperty(api,
                    $"pageextension {extId} of page {pageId} was compiled from source, but BC's emitted delta "
                    + "document for it is not in the metadata registry, so what it adds or modifies cannot be read");
            yield return (extId, xml);
        }
    }

    /// <summary>
    /// The part a source-compiled pageextension of <paramref name="pageId"/> adds under
    /// <paramref name="controlId"/>, or null when none does (#4876). The runner's MasterPage
    /// carries no extension delta, so the part is read from the extension's own delta document
    /// through BC's own parser, <c>NavAppObjectMetadataRuntimeDeltas.FromXml</c>, walking its
    /// <c>ControlAddDelta</c>s as <c>CachingDatabaseDeltaRetrieverHelper.CollectAddedControls</c>
    /// does.
    /// </summary>
    internal static Microsoft.Dynamics.Nav.Types.Metadata.InfopartPageDefinition? SourcePageExtensionPart(int pageId, int controlId)
        => SourcePageExtensionParts(pageId, $"TestPage part {controlId} (page {pageId})")
            .FirstOrDefault(part => part.ID == controlId);

    /// <summary>Every part the source-compiled pageextensions of <paramref name="pageId"/> add
    /// (#4887: the host's eager part build lists them with its own).</summary>
    internal static IEnumerable<Microsoft.Dynamics.Nav.Types.Metadata.InfopartPageDefinition> SourcePageExtensionParts(
        int pageId, string? api = null)
    {
        var extensionIds = GetPageExtensionIdsForPage(pageId).Where(_parsedPageExtensions.ContainsKey).ToList();
        foreach (var (_, xml) in SourcePageExtensionDeltaDocuments(pageId, extensionIds, api ?? $"TestPage parts (page {pageId})"))
            foreach (var part in _partsByDeltaDocument.GetValue(xml, AddedParts))
                yield return part;
    }

    /// <summary>
    /// The AL name of a control a source-compiled pageextension of <paramref name="pageId"/>
    /// adds under <paramref name="controlId"/>, or null when none does (#3458). Read from the
    /// extension's delta document, because the runner's MasterPage carries no extension delta.
    /// </summary>
    internal static string? SourcePageExtensionControlName(int pageId, int controlId)
    {
        var extensionIds = GetPageExtensionIdsForPage(pageId).Where(_parsedPageExtensions.ContainsKey).ToList();
        foreach (var (_, xml) in SourcePageExtensionDeltaDocuments(pageId, extensionIds, $"TestPage control {controlId} name (page {pageId})"))
        {
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            foreach (XmlNode node in doc.DocumentElement!.ChildNodes)
                if (node is XmlElement e && e.Name == "ControlAdd" && FindAddedControlName(e, controlId) is { } name)
                    return name;
        }
        return null;
    }

    private static string? FindAddedControlName(XmlElement parent, int controlId)
    {
        foreach (XmlNode node in parent.ChildNodes)
        {
            if (node is not XmlElement e || e.Name != "Controls") continue;
            if (ReadBcAttrInt(e, "ID") == controlId)
                return string.IsNullOrEmpty(e.GetAttribute("Name")) ? null : e.GetAttribute("Name");
            if (FindAddedControlName(e, controlId) is { } nested) return nested;
        }
        return null;
    }

    // Keyed on the registry's own string, so a --watch/--server reload, which registers new
    // documents, cannot serve a previous generation's parts.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string,
        List<Microsoft.Dynamics.Nav.Types.Metadata.InfopartPageDefinition>> _partsByDeltaDocument = new();

    private static List<Microsoft.Dynamics.Nav.Types.Metadata.InfopartPageDefinition> AddedParts(string deltaXml)
    {
        var parts = new List<Microsoft.Dynamics.Nav.Types.Metadata.InfopartPageDefinition>();
        var deltas = Microsoft.Dynamics.Nav.Apps.MetadataDeltas.NavAppObjectMetadataRuntimeDeltas.FromXml(
            System.Xml.Linq.XDocument.Parse(deltaXml));
        foreach (var delta in deltas.AllDeltas)
            if (delta != null && ControlAddDeltaType.IsInstanceOfType(delta))
                CollectParts(AddedContent(delta), parts);
        return parts;
    }

    private const string ExtensionPartSurface = "TestPage part a pageextension adds (#4876)";

    // ControlAddDelta is internal to BC's delta assembly; its Context.Content is what
    // CachingDatabaseDeltaRetrieverHelper.CollectAddedControls walks.
    private static readonly Lazy<Type> _controlAddDeltaType = new(() =>
        typeof(Microsoft.Dynamics.Nav.Apps.MetadataDeltas.NavAppObjectMetadataRuntimeDeltas).Assembly
            .GetType("Microsoft.Dynamics.Nav.Apps.MetadataDeltas.ControlAddDelta")
        ?? throw new BcShapeGapException(ExtensionPartSurface,
            "Microsoft.Dynamics.Nav.Apps.MetadataDeltas.ControlAddDelta", "the type is not in BC's delta assembly"));

    private static Type ControlAddDeltaType => _controlAddDeltaType.Value;

    private static Microsoft.Dynamics.Nav.Types.Metadata.ControlBaseDefinition? AddedContent(object controlAddDelta)
    {
        var context = MostDerivedProperty(ControlAddDeltaType, "Context").GetValue(controlAddDelta)
            ?? throw new BcShapeGapException(ExtensionPartSurface, "ControlAddDelta.Context", "BC's parser left it null");
        return MostDerivedProperty(context.GetType(), "Content").GetValue(context)
            as Microsoft.Dynamics.Nav.Types.Metadata.ControlBaseDefinition;
    }

    // Context and Content are declared on generic bases and re-declared (new) on the derived
    // types, so a bare GetProperty is ambiguous; take the most derived, which is what an
    // ordinary member access binds.
    private static System.Reflection.PropertyInfo MostDerivedProperty(Type type, string name)
    {
        for (var t = type; t != null; t = t.BaseType)
            if (t.GetProperty(name, BcShape.AnyInstance | System.Reflection.BindingFlags.DeclaredOnly) is { } p)
                return p;
        throw new BcShapeGapException(ExtensionPartSurface, $"{type.Name}.{name}", "the property is gone");
    }

    private static void CollectParts(Microsoft.Dynamics.Nav.Types.Metadata.ControlBaseDefinition? control,
        List<Microsoft.Dynamics.Nav.Types.Metadata.InfopartPageDefinition> into)
    {
        if (control is Microsoft.Dynamics.Nav.Types.Metadata.InfopartPageDefinition part) into.Add(part);
        if (control is Microsoft.Dynamics.Nav.Types.Metadata.ControlGroupBaseDefinition group)
            foreach (var child in group.Controls)
                CollectParts(child, into);
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
