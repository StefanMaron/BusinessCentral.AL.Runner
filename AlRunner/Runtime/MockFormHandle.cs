using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Runtime;

/// <summary>
/// Stub for <c>NavFormHandle</c> / AL's <c>Page "X"</c> variable.
///
/// Two capabilities:
///
/// 1. **Declaration site**: BC emits <c>new NavFormHandle(this, pageId)</c>
///    in the containing scope's field initialiser; the real ctor wants
///    ITreeObject + NavForm which don't exist standalone. The rewriter
///    rewrites the ctor call to <c>new MockFormHandle(pageId)</c>.
///
/// 2. **Procedure dispatch**: when the AL code calls a plain helper
///    procedure on the page variable (<c>P.FormatText(...)</c>), BC
///    lowers it to <c>p.Invoke(memberId, args)</c>. This class mirrors
///    MockCodeunitHandle's dispatch strategy: reflect over the
///    generated <c>Page{pageId}</c> class, find the method whose scope
///    class name carries the matching memberId, and invoke it. No UI
///    lifecycle; triggers and layout are still skipped.
/// </summary>
public class MockFormHandle
{
    public int PageId { get; }

    private object? _pageInstance;

    public MockFormHandle() { }

    public MockFormHandle(int pageId) { PageId = pageId; }

    public void Run() { }

    /// <summary>
    /// Intercepts Page.RunModal(). If a ModalPageHandler is registered for this page,
    /// creates a MockTestPageHandle, invokes the handler, and returns the FormResult
    /// set by the handler (via OK/Cancel action invocation). If no handler is registered,
    /// throws an error so tests fail clearly instead of silently returning a default.
    /// </summary>
    public FormResult RunModal()
    {
        return HandlerRegistry.InvokeModalPageHandler(PageId);
    }
    public void SetRecord(object? record) { }
    public object? GetRecord() => null;
    /// <summary>1-arg GetRecord: BC emits <c>p.Target.GetRecord(rec.Target)</c> to copy the page's
    /// current record into the caller's variable. No-op in standalone mode.</summary>
    public void GetRecord(MockRecordHandle rec) { }
    /// <summary>No-op. BC emits <c>p.Target.SetTableView(rec.Target)</c> to filter the page's view.</summary>
    public void SetTableView(MockRecordHandle rec) { }
    /// <summary>Stub property. BC emits <c>p.Target.LookupMode</c> get/set.</summary>
    public bool LookupMode { get; set; }
    /// <summary>Stub property. BC emits <c>p.Target.PromptMode</c> get/set (NavOption = Enum "Prompt Mode").</summary>
    public NavOption? PromptMode { get; set; }
    /// <summary>Stub property. BC emits <c>p.Target.Editable</c> get/set. Default true.</summary>
    public bool Editable { get; set; } = true;
    /// <summary>Stub property. BC emits <c>p.Target.PageCaption</c> get/set.</summary>
    public string PageCaption { get; set; } = "";
    /// <summary>No-op. BC emits <c>p.Clear()</c> (on handle, not .Target).</summary>
    public void Clear() { }
    public void Update(bool saveRecord = true) { }
    public void Close() { }
    /// <summary>No-op. Activates the page. BC emits optional bool arg for changeToEditMode.</summary>
    public void Activate(bool changeToEditMode = false) { }
    /// <summary>No-op. Saves the current record on the page.</summary>
    public void SaveRecord() { }
    /// <summary>No-op. Filters the page view to the selection in the record variable.</summary>
    public void SetSelectionFilter(MockRecordHandle rec) { }
    /// <summary>Returns the page's object ID as text. Stub returns empty string.
    /// BC emits the method as <c>ObjectID</c> (uppercase D).</summary>
    public NavText ObjectID(bool withCaption = false) => NavText.Empty;

    /// <summary>
    /// Extension-scoped Invoke — called when invoking a method defined in a page
    /// extension. The BC compiler emits (extensionId, memberId, args).
    /// We search the PageExtension{extensionId} class for the method.
    /// </summary>
    public object? Invoke(int extensionId, int memberId, object[] args)
    {
        // Ensure the base page instance exists (extension methods may reference it)
        Type? pageType = MockRecordHandle.FindTypeAcrossAssemblies($"Page{PageId}");
        if (pageType != null)
            EnsurePageInstance(pageType);

        // Try the extension type first
        Type? extType = MockRecordHandle.FindTypeAcrossAssemblies($"PageExtension{extensionId}");
        if (extType != null)
        {
            var result = DispatchByScope(extType, memberId, args);
            if (result.Found)
                return result.Value;
        }

        // Fall back to the base page type
        return Invoke(memberId, args);
    }

    /// <summary>Dispatch a plain helper procedure on the page's generated class.</summary>
    public object? Invoke(int memberId, object[] args)
    {
        Type? pageType = MockRecordHandle.FindTypeAcrossAssemblies($"Page{PageId}");
        if (pageType == null) return null;

        EnsurePageInstance(pageType);

        var result = DispatchByScope(pageType, memberId, args);
        if (result.Found)
            return result.Value;

        // Fallback: match by argument count across public methods.
        var candidateMethods = pageType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .Where(m => m.GetParameters().Length == args.Length && !m.IsSpecialName && m.DeclaringType == pageType)
            .ToArray();
        if (candidateMethods.Length == 1)
        {
            var method = candidateMethods[0];
            var parameters = method.GetParameters();
            var convertedArgs = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
                if (i < args.Length) convertedArgs[i] = args[i];
            return method.Invoke(_pageInstance, convertedArgs);
        }
        return null;
    }

    private void EnsurePageInstance(Type pageType)
    {
        if (_pageInstance == null)
        {
            // Use GetUninitializedObject so page classes with no parameterless constructor
            // (those whose ctor required ITreeObject parent) still work after the
            // constructor is stripped by the rewriter.
            _pageInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(pageType);
            // Run InitializeComponent to initialise global-variable fields such as MockArray
            // (e.g. myCaption in issue #1232). This is the rewriter-preserved clean version
            // that omits NavForm-specific calls.
            var initMethod = pageType.GetMethod("InitializeComponent",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            try { initMethod?.Invoke(_pageInstance, null); } catch { }
            // After InitializeComponent, walk all instance fields and initialize any that
            // are still null. This covers auto-property backing fields whose initializer
            // (e.g. "Rec { get; } = new MockRecordHandle(tableId)") only runs in the
            // constructor — which GetUninitializedObject skips.
            // For MockRecordHandle fields we use the correct source-table ID derived from
            // the page ID via TableFieldRegistry, rather than the default table-0 fallback
            // in AlCompat.InitializeUninitializedObject (issue #1422).
            var sourceTableId = TableFieldRegistry.GetSourceTableId(PageId) ?? 0;
            foreach (var field in pageType.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.FieldType.IsValueType) continue;
                if (field.GetValue(_pageInstance) != null) continue;
                if (field.FieldType == typeof(MockRecordHandle))
                {
                    field.SetValue(_pageInstance, new MockRecordHandle(sourceTableId));
                    continue;
                }
                try
                {
                    var ctor = field.FieldType.GetConstructor(Type.EmptyTypes);
                    if (ctor != null)
                        field.SetValue(_pageInstance, ctor.Invoke(null));
                }
                catch { }
            }
        }
    }

    /// <summary>
    /// Dispatch a method call by searching for a nested scope type whose name contains
    /// the member ID. Returns (true, result) if found, (false, null) otherwise.
    /// </summary>
    private (bool Found, object? Value) DispatchByScope(Type type, int memberId, object[] args)
    {
        var absMemberId = System.Math.Abs(memberId).ToString();
        var memberIdStr = memberId.ToString();

        // Create instance of the type if different from the page instance
        object? instance = null;
        if (type == _pageInstance?.GetType())
            instance = _pageInstance;
        else
        {
            instance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
            // Extension classes need a reference to the parent page instance
            var parentField = type.GetField("_parent", BindingFlags.NonPublic | BindingFlags.Instance)
                           ?? type.BaseType?.GetField("_parent", BindingFlags.NonPublic | BindingFlags.Instance);
            if (parentField != null && _pageInstance != null)
                parentField.SetValue(instance, _pageInstance);
        }

        foreach (var nested in type.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (!nested.Name.Contains($"_Scope_{memberIdStr}") &&
                !nested.Name.Contains($"_Scope__{absMemberId}"))
                continue;

            var scopeName = nested.Name;
            var scopeIdx = scopeName.IndexOf("_Scope_");
            if (scopeIdx < 0) continue;
            var methodName = scopeName.Substring(0, scopeIdx);

            var method = type.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (method == null) continue;

            var parameters = method.GetParameters();
            var convertedArgs = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i < args.Length)
                    convertedArgs[i] = args[i];
            }
            return (true, method.Invoke(instance, convertedArgs));
        }

        return (false, null);
    }
}
