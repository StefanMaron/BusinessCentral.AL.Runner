// ApplicationAreaControlRemoval — the application-area half of BC's page element-removal pass,
// run from MetadataProvider.GetMasterPage while the license half stays off (#4750).
//
// OBSERVABLY EQUIVALENT: BC's MetadataProvider.RemoveItemsOnPageBasedOnLicenseAndApplicationArea
// removes a control, part or system part when NavSession.IsApplicationAreaEnabled rejects its
// ApplicationArea; a TestPage then reports the control as not found. This runs BC's own
// RemoveInList walk and BC's own IsApplicationAreaEnabled over the same five control lists,
// with a predicate carrying only the application-area branch of MetadataProvider.RemoveControl.
// The license/permission branches are left out because the runner models a fully licensed
// session (MetadataProviderElementRemoval.cs), where they remove nothing.
// Citation: corpus pageapplicationarea/TestPageApplicationAreaControlRemoval.al (codeunit 67530);
// decompiled bc284 RemoveControl / RemoveInList / NavSession.IsApplicationAreaEnabled, identical
// on bc270.
//
// Trap: when every area is enabled (the session's area string is empty) this returns the page
// untouched, so the empty-group pruning RemoveInList also does is not applied there — that is
// the runner's behaviour from before #4750, deliberately kept for the common case.
//
// Actions (#4795): the same pass's action lists, through BC's own RemoveInList and
// UpdateClonedActionsFromOriginates, with the application-area branches of RemoveAction and its
// four RemoveXxxDefinition helpers (decompiled bc284). Corpus codeunit 67531
// "PAA Area Action Tests".
//
// Request pages (#4829): MetaReport takes the same pass as a delegate and runs it on a fresh
// clone at every RequestFormMetadata read, so the per-report MetaReport cache does not freeze
// one test's areas. NavReportSync binds RemoveFromRequestPage there, as
// MetadataProvider.GetReportMetadata binds `mp => RemoveItemsOnPageBasedOnLicenseAndApplicationArea(mp, null)`
// (decompiled bc284; MetaReport shape identical on 27.0 and 28.5). Corpus codeunit 67533.
//
// Precompiled pages (#4796): their metadata document carries no field controls, so each field
// control's area comes from the symbol file (RecordPatches.DependencyFieldControlAreas) and is
// tested with the same IsApplicationAreaEnabled. Corpus codeunit 67534;
// docs/dependency-page-properties.md#field-control-application-area.
//
// Source-compiled pageextensions (#4866): the MasterPage carries no extension delta, so the
// controls one adds, and the area its modify() sets, come from its own delta document
// (RecordPatches.SourcePageExtensionAreas), on a source-compiled or a precompiled page alike.
// Corpus codeunits 67535 and 67536; actions, actionrefs and parts (#4871): 67538 and 67539.
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types.Metadata;

namespace AlRunner.Patches;

public static class ApplicationAreaControlRemoval
{
    private static int _gateOpen;
    private static int _gateClosed;
    private static MethodInfo? _removeInList;
    private static MethodInfo? _removeInListActions;
    private static Delegate? _actionPredicate;
    private static Delegate? _actionChildren;
    private static MethodInfo? _updateClonedActions;
    private static Delegate? _getControl;
    private static Delegate? _getPart;
    private static Delegate? _predicate;
    private static ConstructorInfo? _builderCtor;
    private static Func<string, string, bool>? _isApplicationAreaEnabled;
    private static Func<object?, bool>? _propertyIsFalse;

    /// <summary>
    /// Binds every BC member this patch calls. Throws when one is missing: a silent miss would
    /// put back "every control is found" with nothing to say so.
    /// </summary>
    public static void Bind(Assembly navNcl)
    {
        const BindingFlags S = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static;
        var provider = navNcl.GetType("Microsoft.Dynamics.Nav.XmlMetadata.MetadataProvider")
            ?? throw Shape("MetadataProvider");

        var optionField = provider.GetField("elementRemovalOption", S) ?? throw Shape("MetadataProvider.elementRemovalOption");
        var noneValue = Convert.ToInt32(Enum.Parse(optionField.FieldType, "None"));
        _gateClosed = noneValue;
        _gateOpen = Enum.GetValues(optionField.FieldType).Cast<object>()
            .Select(Convert.ToInt32).FirstOrDefault(v => v != noneValue, noneValue);
        if (_gateOpen == noneValue) throw Shape("ElementRemovalOption with a member other than None");

        _isApplicationAreaEnabled = (Func<string, string, bool>)Delegate.CreateDelegate(
            typeof(Func<string, string, bool>),
            Required(provider, "IsApplicationAreaEnabled", new[] { typeof(string), typeof(string) }));

        // RemoveInList, GetControl and GetPart are generic method definitions, so their
        // signatures cannot be pinned before MakeGenericMethod; FindMethod refuses a second one.
        var removeInListDefinition = Required(provider, "RemoveInList", null);
        _removeInList = removeInListDefinition.MakeGenericMethod(typeof(ControlBaseDefinition));
        var p = _removeInList.GetParameters();
        if (p.Length != 8) throw Shape("MetadataProvider.RemoveInList with 8 parameters");
        _builderCtor = p[0].ParameterType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(bool) })
            ?? throw Shape($"{p[0].ParameterType.Name}(bool)");

        _predicate = Delegate.CreateDelegate(p[4].ParameterType,
            ((Func<object, object, object?, bool, bool>)RemoveWhenAreaNotEnabled).Method);
        _getControl = Delegate.CreateDelegate(p[5].ParameterType,
            Required(provider, "GetControl", null).MakeGenericMethod(typeof(ControlBaseDefinition)));
        _getPart = Delegate.CreateDelegate(p[5].ParameterType,
            Required(provider, "GetPart", null).MakeGenericMethod(typeof(ControlBaseDefinition)));

        _removeInListActions = removeInListDefinition.MakeGenericMethod(typeof(ActionBaseDefinition));
        var pa = _removeInListActions.GetParameters();
        _actionPredicate = Delegate.CreateDelegate(pa[4].ParameterType,
            ((Func<object, object, object?, bool, bool>)RemoveWhenAreaNotEnabled).Method);
        _actionChildren = Delegate.CreateDelegate(pa[5].ParameterType,
            ((Func<ActionBaseDefinition, IList<ActionBaseDefinition>>)ActionChildren).Method);
        _updateClonedActions = Required(provider, "UpdateClonedActionsFromOriginates", new[] { typeof(MasterPage) });

        var visibleType = (typeof(InfopartPageDefinition).GetProperty(nameof(InfopartPageDefinition.Visible))
            ?? throw Shape("InfopartPageDefinition.Visible")).PropertyType;
        var isFalseSignature = new[] { visibleType };
        var propertyHelper = navNcl.GetTypes().Concat(typeof(MasterPage).Assembly.GetTypes())
            .FirstOrDefault(t => t.Name == "PropertyHelper"
                && BcShape.FindMethod(t, "PropertyIsFalse", S, Surface, "PropertyHelper.PropertyIsFalse",
                    "binds the Visible test of MetadataProvider.RemoveControl", isFalseSignature) != null)
            ?? throw Shape($"PropertyHelper.PropertyIsFalse({visibleType.Name})");
        var isFalse = Required(propertyHelper, "PropertyIsFalse", isFalseSignature);
        _propertyIsFalse = v => (bool)isFalse.Invoke(null, new[] { v })!;
    }

    private const string Surface = "TestPage application-area control removal (#4750)";

    private static MethodInfo Required(Type declaring, string name, Type[]? types) =>
        BcShape.RequiredMethod(declaring, name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static,
            Surface, $"{declaring.Name}.{name}", "the application-area half of BC's page removal pass", types);

    /// <summary>
    /// Replaces GetMasterPage's read of MetadataProvider.elementRemovalOption: open (a member
    /// other than None) only when the session has application areas set.
    /// </summary>
    public static int RemovalGate() =>
        NavCurrentThread.Session is { } session && !session.IsApplicationAreaEnabled(null!)
            ? _gateOpen
            : _gateClosed;

    /// <summary>
    /// Replaces GetMasterPage's call to RemoveItemsOnPageBasedOnLicenseAndApplicationArea.
    /// Mirrors that method's control lists; the removal itself is BC's RemoveInList.
    /// </summary>
    public static object RemoveByApplicationArea(object provider, object masterPage, object? relatedMasterPages)
        => Remove((MasterPage)masterPage, relatedMasterPages, isRequestPage: false);

    private static MasterPage Remove(MasterPage page, object? relatedMasterPages, bool isRequestPage)
    {
        if (_removeInList == null) throw new InvalidOperationException("ApplicationAreaControlRemoval.Bind was not called");
        if (NavCurrentThread.Session is not { } session || session.IsApplicationAreaEnabled(null!)) return page;

        var builder = _builderCtor!.Invoke(new object[] { false });
        var removed = new HashSet<int>();
        var removedActions = new HashSet<int>();
        _removing = removed;
        _removingActions = removedActions;
        // Not for a request page: its ID is a report's, which can equal a page's.
        var extensionAreas = isRequestPage ? null : RecordPatches.SourcePageExtensionAreas(page.ID);
        // A request page's id is its report's: its extensions are reportextensions (#4896).
        var requestPageExtension = isRequestPage ? RecordPatches.SourceReportExtensionRequestPageAreas(page.ID) : default;
        _areaChanges = isRequestPage ? requestPageExtension.AreaChanges : extensionAreas?.AreaChanges;
        _actionAreaChanges = extensionAreas?.ActionAreaChanges;
        void Remove(IList<ControlBaseDefinition>? controls, Delegate selector)
        {
            if (controls == null) return;
            _removeInList.Invoke(null, new object?[]
            {
                builder, false, page.RemovedControls, controls, _predicate, selector, relatedMasterPages, true,
            });
        }
        void RemoveActions(IList<ActionBaseDefinition>? actions)
        {
            if (actions == null) return;
            _removeInListActions!.Invoke(null, new object?[]
            {
                builder, false, page.RemovedControls, actions, _actionPredicate, _actionChildren, relatedMasterPages, true,
            });
        }

        // BC's order: cloned actions first, then the command bar, content, per-control actions,
        // and the remaining control lists.
        page = (MasterPage)_updateClonedActions!.Invoke(null, new object[] { page })!;
        try
        {
            RemoveActions(page.CommandBar?.Actions);
            Remove(page.ContentArea.Controls, _getControl!);
            foreach (var placeholder in page.ContentArea.Controls.OfType<ControlContainerPlaceHolder>())
                for (var i = placeholder.Controls.Count - 1; i >= 0; i--)
                    RemoveActions(placeholder.Controls[i].Actions);
            Remove(page.PromptArea?.Controls, _getControl!);
            Remove(page.PromptOptionsArea?.Controls, _getControl!);
            Remove(page.UserControlHostNavigationArea?.Controls, _getControl!);
            Remove(page.InfopartsArea?.Controls, _getPart!);
            // A precompiled page's metadata carries no field controls (DependencyPageMetadataXml),
            // so their areas come from its symbol file, through the same BC predicate (#4796).
            // A precompiled report's request page carries no field controls either (#4863).
            if (isRequestPage)
            {
                bool IsEnabled(string? area) => _isApplicationAreaEnabled!(area!, null!);
                removed.UnionWith(DependencyRequestPageFieldsToRemove(page.ID, IsEnabled, requestPageExtension.AreaChanges));
                removed.UnionWith(Rejected(requestPageExtension.AddedFields, requestPageExtension.AreaChanges, IsEnabled));
            }
            if (extensionAreas != null)
            {
                bool IsEnabled(string? area) => _isApplicationAreaEnabled!(area!, null!);
                removed.UnionWith(DependencyFieldControlsToRemove(page.ID, IsEnabled, extensionAreas.AreaChanges));
                removed.UnionWith(SourceExtensionFieldControlsToRemove(extensionAreas, IsEnabled));
                removed.UnionWith(Rejected(extensionAreas.AddedParts, NoAreaChanges, IsEnabled));
                removedActions.UnionWith(Rejected(extensionAreas.AddedActions, extensionAreas.ActionAreaChanges, IsEnabled));
                // A modify() replaces the target's area outright, so its verdict needs no base
                // area. That is what reaches a precompiled page's action, which the runner's
                // MasterPage does not carry (#4862).
                removedActions.UnionWith(RejectedChanges(extensionAreas.ActionAreaChanges, IsEnabled));
                // A precompiled page's own actions, from its symbol file (#4862).
                removedActions.UnionWith(DependencyActionsToRemove(page.ID, IsEnabled, extensionAreas.ActionAreaChanges));
                // An actionref follows its target: ActionRefDefinition.SolveApplicationArea gives it
                // the target's area at runtime (corpus 67531, 67538).
                removedActions.UnionWith(RejectedActionRefs(extensionAreas.AddedActionRefs, removedActions, IsEnabled));
            }
        }
        finally
        {
            _removing = null;
            _removingActions = null;
            _areaChanges = null;
            _actionAreaChanges = null;
        }
        if (removed.Count > 0) RemovedIds.AddOrUpdate(page, removed);
        if (removedActions.Count > 0) RemovedActionIds.AddOrUpdate(page, removedActions);
        return page;
    }

    /// <summary>
    /// MetaReport's RemoveItemsOnPageBasedOnLicenseAndApplicationArea delegate (#4829). A page
    /// with no ContentArea is the runner's own request-page stub (NavReportSync), left as is.
    /// </summary>
    public static MasterPage RemoveFromRequestPage(MasterPage page) =>
        page.ContentArea == null ? page : Remove(page, null, isRequestPage: true);

    /// <summary>
    /// Whether this page's application-area pass removed the control. The runner's TestPage
    /// resolves controls from compile-time maps rather than from the MasterPage, so it asks
    /// here and answers null, which BC's NavTestPageBase.GetField turns into its own
    /// "The field with ID = ... is not found on the page." error.
    /// </summary>
    public static bool WasRemoved(MasterPage? page, int controlId) =>
        page != null && RemovedIds.TryGetValue(page, out var ids) && ids.Contains(controlId);

    /// <summary>
    /// Whether this page's application-area pass removed the action. LiveNavTestPage.GetAction
    /// answers null for it, which BC's NavTestPageBase.GetAction turns into its own
    /// NavTestActionNotFoundException.
    /// </summary>
    public static bool WasActionRemoved(MasterPage? page, int actionId) =>
        page != null && RemovedActionIds.TryGetValue(page, out var ids) && ids.Contains(actionId);

    /// <summary>
    /// The field controls of a precompiled page that <paramref name="isAreaEnabled"/> rejects —
    /// RemoveControl's ControlDefinition arm, over areas read from the page's symbol file. A
    /// symbol-file control has no ResourceIdentifier, so the area alone decides (#4796).
    /// </summary>
    internal static IEnumerable<int> DependencyFieldControlsToRemove(
        int pageId, Func<string?, bool> isAreaEnabled, IReadOnlyDictionary<int, string>? areaChanges = null)
        => RecordPatches.DependencyFieldControlAreas(pageId)
            .Where(control => !isAreaEnabled(AreaAfterSourceChange(pageId, control, areaChanges)))
            .Select(control => control.Id);

    // A precompiled and a source extension modify()ing one control to different areas refuse, as
    // two extensions within either set do: which BC applies has not been measured (#4866).
    private static string? AreaAfterSourceChange(
        int pageId, (int Id, string? ApplicationArea, string? ModifiedArea) control, IReadOnlyDictionary<int, string>? areaChanges)
    {
        if (areaChanges == null || !areaChanges.TryGetValue(control.Id, out var changed)) return control.ApplicationArea;
        if (control.ModifiedArea != null && !string.Equals(control.ModifiedArea, changed, StringComparison.Ordinal))
            throw TestPageShapeGap.ControlProperty(
                $"TestPage ApplicationArea on page {pageId} control {control.Id}",
                $"a precompiled pageextension modifies it to '{control.ModifiedArea}' and a source pageextension "
                + $"to '{changed}', and which one BC applies has not been measured (#4866)");
        return changed;
    }

    /// <summary>
    /// The actions of a precompiled page that <paramref name="isAreaEnabled"/> rejects (#4862):
    /// an action by its area after a source <c>modify()</c>; an actionref exactly when its target
    /// is, because ActionRefDefinition.SolveApplicationArea gives it its target's area. A precompiled and a source
    /// <c>modify()</c> of one action to different areas refuse (#4866's rule).
    /// </summary>
    internal static IEnumerable<int> DependencyActionsToRemove(
        int pageId, Func<string?, bool> isAreaEnabled, IReadOnlyDictionary<int, string>? areaChanges = null)
    {
        var actions = RecordPatches.DependencyActionAreas(pageId);
        var rejected = new HashSet<int>();
        foreach (var action in actions)
        {
            if (action.Kind == 4) continue;
            if (!isAreaEnabled(AreaAfterSourceChange(pageId, (action.Id, action.ApplicationArea, action.ModifiedArea), areaChanges)))
                rejected.Add(action.Id);
        }
        foreach (var actionRef in actions)
        {
            if (actionRef.Kind != 4) continue;
            if (actionRef.TargetId is { } target && rejected.Contains(target))
                rejected.Add(actionRef.Id);
        }
        return rejected;
    }

    /// <summary>
    /// The request-page field controls of a precompiled report that <paramref name="isAreaEnabled"/>
    /// rejects (#4863). Keyed by REPORT id, which is what a request page's MasterPage.ID is.
    /// </summary>
    internal static IEnumerable<int> DependencyRequestPageFieldsToRemove(
        int reportId, Func<string?, bool> isAreaEnabled, IReadOnlyDictionary<int, string>? areaChanges = null)
        => RecordPatches.DependencyRequestPageFieldAreas(reportId)
            .Where(control => !isAreaEnabled(AreaAfterSourceChange(reportId, control, areaChanges)))
            .Select(control => control.Id);

    /// <summary>
    /// The field controls source-compiled pageextensions add that <paramref name="isAreaEnabled"/>
    /// rejects: a control's own area, or none — it does not take the base page's (#4866).
    /// </summary>
    internal static IEnumerable<int> SourceExtensionFieldControlsToRemove(
        RecordPatches.SourcePageExtensionAreaSet areas, Func<string?, bool> isAreaEnabled)
        => Rejected(areas.AddedFieldControls, areas.AreaChanges, isAreaEnabled);

    /// <summary>
    /// The parts and actions source-compiled pageextensions add that <paramref name="isAreaEnabled"/>
    /// rejects, by the same rule as their field controls (#4871).
    /// </summary>
    internal static (IEnumerable<int> Parts, IEnumerable<int> Actions) SourceExtensionPartsAndActionsToRemove(
        RecordPatches.SourcePageExtensionAreaSet areas, Func<string?, bool> isAreaEnabled)
        => (Rejected(areas.AddedParts, NoAreaChanges, isAreaEnabled),
            Rejected(areas.AddedActions, areas.ActionAreaChanges, isAreaEnabled));

    internal static IEnumerable<int> RejectedActionRefs(
        IEnumerable<(int Id, string? ApplicationArea, int TargetId)> actionRefs,
        IReadOnlySet<int> removedActions, Func<string?, bool> isAreaEnabled)
        => actionRefs
            .Where(actionRef => removedActions.Contains(actionRef.TargetId))
            .Select(actionRef => actionRef.Id)
            .ToList();

    private static readonly IReadOnlyDictionary<int, string> NoAreaChanges = new Dictionary<int, string>();

    private static IEnumerable<int> RejectedChanges(IReadOnlyDictionary<int, string> areaChanges, Func<string?, bool> isAreaEnabled)
        => areaChanges.Where(change => !isAreaEnabled(change.Value)).Select(change => change.Key);

    private static IEnumerable<int> Rejected(IEnumerable<(int Id, string? ApplicationArea)> elements,
        IReadOnlyDictionary<int, string> areaChanges, Func<string?, bool> isAreaEnabled)
        => elements
            .Where(element => !isAreaEnabled(AreaAfterChanges(element.Id, element.ApplicationArea, areaChanges)))
            .Select(element => element.Id);

    private static string? AreaAfterChanges(int controlId, string? area, IReadOnlyDictionary<int, string>? areaChanges)
        => areaChanges != null && areaChanges.TryGetValue(controlId, out var changed) ? changed : area;

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MasterPage, HashSet<int>> RemovedIds = new();
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MasterPage, HashSet<int>> RemovedActionIds = new();

    [ThreadStatic] private static HashSet<int>? _removing;
    [ThreadStatic] private static HashSet<int>? _removingActions;
    [ThreadStatic] private static IReadOnlyDictionary<int, string>? _areaChanges;
    [ThreadStatic] private static IReadOnlyDictionary<int, string>? _actionAreaChanges;

    private static IList<ActionBaseDefinition> ActionChildren(ActionBaseDefinition node) => node.Actions;

    private static bool RemoveWhenAreaNotEnabled(object builder, object element, object? related, bool applyApplicationArea)
    {
        var remove = AreaNotEnabled(element, applyApplicationArea);
        // ActionBaseDefinition derives from ControlBaseDefinition, so it is tested first.
        if (remove && element is ActionBaseDefinition action) _removingActions?.Add(action.ID);
        else if (remove && element is ControlBaseDefinition control) _removing?.Add(control.ID);
        return remove;
    }

    // The application-area branches of MetadataProvider.RemoveControl, in its order.
    private static bool AreaNotEnabled(object element, bool applyApplicationArea)
    {
        if (!applyApplicationArea) return false;
        switch (element)
        {
            case ControlDefinition control:
                return !_isApplicationAreaEnabled!(
                    AreaAfterChanges(control.ID, control.ApplicationArea, _areaChanges)!, control.ResourceIdentifier);
            // A pageextension cannot modify() a part's or system part's ApplicationArea (AL0246),
            // so no source change applies here.
            case InfopartPageDefinition part when !_propertyIsFalse!(part.Visible):
                return !_isApplicationAreaEnabled!(part.ApplicationArea, part.ResourceIdentifier);
            case InfopartSystemDefinition systemPart:
                return !_isApplicationAreaEnabled!(systemPart.ApplicationArea, systemPart.ResourceIdentifier);
            // MetadataProvider.RemoveAction dispatches to these four; each checks the area first.
            case ActionDefinition action:
                return !_isApplicationAreaEnabled!(
                    AreaAfterChanges(action.ID, action.ApplicationArea, _actionAreaChanges)!, action.ResourceIdentifier);
            case CustomActionDefinition customAction:
                return !_isApplicationAreaEnabled!(
                    AreaAfterChanges(customAction.ID, customAction.ApplicationArea, _actionAreaChanges)!, customAction.ResourceIdentifier);
            case FileUploadActionDefinition fileUploadAction:
                return !_isApplicationAreaEnabled!(
                    AreaAfterChanges(fileUploadAction.ID, fileUploadAction.ApplicationArea, _actionAreaChanges)!, fileUploadAction.ResourceIdentifier);
            // RemoveActionRefDefinition: its own area, then its target's. A missing target is
            // BC's structural removal, not an area one, and is left out with the license half.
            case ActionRefDefinition actionRef:
                return !_isApplicationAreaEnabled!(actionRef.ApplicationArea, actionRef.ResourceIdentifier)
                    || (actionRef.TargetActionDefinition is { } target && AreaNotEnabled(target, applyApplicationArea));
            default:
                return false;
        }
    }

    private static InvalidOperationException Shape(string what) =>
        new($"{what} not found — BC metadata shape changed; do not commit (ApplicationAreaControlRemoval, #4750)");
}
