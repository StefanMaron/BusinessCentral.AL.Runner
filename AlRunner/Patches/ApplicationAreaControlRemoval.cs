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
// the runner's behaviour from before #4750, deliberately kept for the common case. Actions are
// not filtered yet (#4750's follow-up); see the PR body.
using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types.Metadata;

namespace AlRunner.Patches;

public static class ApplicationAreaControlRemoval
{
    private static int _gateOpen;
    private static int _gateClosed;
    private static MethodInfo? _removeInList;
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
            provider.GetMethod("IsApplicationAreaEnabled", S, new[] { typeof(string), typeof(string) })
                ?? throw Shape("MetadataProvider.IsApplicationAreaEnabled(string, string)"));

        var removeInList = provider.GetMethod("RemoveInList", S) ?? throw Shape("MetadataProvider.RemoveInList");
        _removeInList = removeInList.MakeGenericMethod(typeof(ControlBaseDefinition));
        var p = _removeInList.GetParameters();
        if (p.Length != 8) throw Shape("MetadataProvider.RemoveInList with 8 parameters");
        _builderCtor = p[0].ParameterType.GetConstructor(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, new[] { typeof(bool) })
            ?? throw Shape($"{p[0].ParameterType.Name}(bool)");

        _predicate = Delegate.CreateDelegate(p[4].ParameterType,
            typeof(ApplicationAreaControlRemoval).GetMethod(nameof(RemoveWhenAreaNotEnabled), S)!);
        _getControl = Delegate.CreateDelegate(p[5].ParameterType,
            (provider.GetMethod("GetControl", S) ?? throw Shape("MetadataProvider.GetControl"))
                .MakeGenericMethod(typeof(ControlBaseDefinition)));
        _getPart = Delegate.CreateDelegate(p[5].ParameterType,
            (provider.GetMethod("GetPart", S) ?? throw Shape("MetadataProvider.GetPart"))
                .MakeGenericMethod(typeof(ControlBaseDefinition)));

        var propertyHelper = navNcl.GetTypes().FirstOrDefault(t => t.Name == "PropertyHelper"
                && t.GetMethod("PropertyIsFalse", S) != null)
            ?? typeof(MasterPage).Assembly.GetTypes().FirstOrDefault(t => t.Name == "PropertyHelper"
                && t.GetMethod("PropertyIsFalse", S) != null)
            ?? throw Shape("PropertyHelper.PropertyIsFalse");
        var isFalse = propertyHelper.GetMethods(S).Single(m => m.Name == "PropertyIsFalse" && m.GetParameters().Length == 1);
        _propertyIsFalse = v => (bool)isFalse.Invoke(null, new[] { v })!;
    }

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
    {
        var page = (MasterPage)masterPage;
        if (_removeInList == null) throw new InvalidOperationException("ApplicationAreaControlRemoval.Bind was not called");
        if (NavCurrentThread.Session is not { } session || session.IsApplicationAreaEnabled(null!)) return page;

        var builder = _builderCtor!.Invoke(new object[] { false });
        var removed = new HashSet<int>();
        _removing = removed;
        void Remove(IList<ControlBaseDefinition>? controls, Delegate selector)
        {
            if (controls == null) return;
            _removeInList.Invoke(null, new object?[]
            {
                builder, false, page.RemovedControls, controls, _predicate, selector, relatedMasterPages, true,
            });
        }

        try
        {
            Remove(page.ContentArea.Controls, _getControl!);
            Remove(page.PromptArea?.Controls, _getControl!);
            Remove(page.PromptOptionsArea?.Controls, _getControl!);
            Remove(page.UserControlHostNavigationArea?.Controls, _getControl!);
            Remove(page.InfopartsArea?.Controls, _getPart!);
        }
        finally
        {
            _removing = null;
        }
        if (removed.Count > 0) RemovedIds.AddOrUpdate(page, removed);
        return page;
    }

    /// <summary>
    /// Whether this page's application-area pass removed the control. The runner's TestPage
    /// resolves controls from compile-time maps rather than from the MasterPage, so it asks
    /// here and answers null, which BC's NavTestPageBase.GetField turns into its own
    /// "The field with ID = ... is not found on the page." error.
    /// </summary>
    public static bool WasRemoved(MasterPage? page, int controlId) =>
        page != null && RemovedIds.TryGetValue(page, out var ids) && ids.Contains(controlId);

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MasterPage, HashSet<int>> RemovedIds = new();

    [ThreadStatic] private static HashSet<int>? _removing;

    private static bool RemoveWhenAreaNotEnabled(object builder, object element, object? related, bool applyApplicationArea)
    {
        var remove = AreaNotEnabled(element, applyApplicationArea);
        if (remove && element is ControlBaseDefinition control) _removing?.Add(control.ID);
        return remove;
    }

    // The application-area branches of MetadataProvider.RemoveControl, in its order.
    private static bool AreaNotEnabled(object element, bool applyApplicationArea)
    {
        if (!applyApplicationArea) return false;
        switch (element)
        {
            case ControlDefinition control:
                return !_isApplicationAreaEnabled!(control.ApplicationArea, control.ResourceIdentifier);
            case InfopartPageDefinition part when !_propertyIsFalse!(part.Visible):
                return !_isApplicationAreaEnabled!(part.ApplicationArea, part.ResourceIdentifier);
            case InfopartSystemDefinition systemPart:
                return !_isApplicationAreaEnabled!(systemPart.ApplicationArea, systemPart.ResourceIdentifier);
            default:
                return false;
        }
    }

    private static InvalidOperationException Shape(string what) =>
        new($"{what} not found — BC metadata shape changed; do not commit (ApplicationAreaControlRemoval, #4750)");
}
