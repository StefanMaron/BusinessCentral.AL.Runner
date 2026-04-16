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

    /// <summary>Dispatch a plain helper procedure on the page's generated class.</summary>
    public object? Invoke(int memberId, object[] args)
    {
        var assembly = MockCodeunitHandle.CurrentAssembly;
        if (assembly == null) return null;

        Type? pageType = null;
        foreach (var t in assembly.GetTypes())
        {
            if (t.Name == $"Page{PageId}") { pageType = t; break; }
        }
        if (pageType == null) return null;

        if (_pageInstance == null)
        {
            _pageInstance = System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(pageType);
            var initMethod = pageType.GetMethod("InitializeComponent",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            initMethod?.Invoke(_pageInstance, null);
        }

        // Match the same scope-name encoding MockCodeunitHandle uses.
        var absMemberId = System.Math.Abs(memberId).ToString();
        var memberIdStr = memberId.ToString();

        foreach (var nested in pageType.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (nested.Name.Contains($"_Scope_{memberIdStr}") ||
                nested.Name.Contains($"_Scope__{absMemberId}"))
            {
                var scopeName = nested.Name;
                var scopeIdx = scopeName.IndexOf("_Scope_");
                if (scopeIdx < 0) continue;
                var methodName = scopeName.Substring(0, scopeIdx);

                var method = pageType.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (method == null) continue;

                var parameters = method.GetParameters();
                var convertedArgs = new object?[parameters.Length];
                for (int i = 0; i < parameters.Length; i++)
                {
                    if (i < args.Length)
                        convertedArgs[i] = args[i];
                }
                return method.Invoke(_pageInstance, convertedArgs);
            }
        }

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
}
