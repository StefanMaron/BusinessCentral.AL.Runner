using System.Reflection;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

namespace AlRunner.Runtime;

/// <summary>
/// Registry for test handler functions (ConfirmHandler, MessageHandler, ModalPageHandler, etc.).
///
/// In BC, test codeunits declare handler functions with attributes like
/// [ConfirmHandler], [MessageHandler], and [ModalPageHandler]. When the code
/// under test calls Confirm(), Message(), or Page.RunModal(), the BC test
/// framework dispatches to the registered handler instead of showing UI.
///
/// The Executor registers handlers before each test by reading the Handlers
/// property from the [NavTest] attribute and finding matching [NavHandler]
/// methods on the test codeunit.
/// </summary>
public static class HandlerRegistry
{
    // The parent codeunit instance (test codeunit) that owns the handler methods
    private static object? _parentInstance;

    // Registered confirm handler: method that takes (NavText question, ByRef<bool> reply)
    private static MethodInfo? _confirmHandler;

    // Registered message handler: method that takes (NavText msg)
    private static MethodInfo? _messageHandler;

    // Registered modal page handler: method that takes (MockTestPageHandle testPage)
    private static MethodInfo? _modalPageHandler;

    // Registered request page handler: method that takes (MockTestPageHandle testPage)
    private static MethodInfo? _requestPageHandler;

    // Registered report handler: method that takes (MockTestPageHandle testPage)
    private static MethodInfo? _reportHandler;

    // Registered send notification handler: method that takes (ByRef<MockNotification> notification) and returns bool
    private static MethodInfo? _sendNotificationHandler;

    /// <summary>
    /// Register handlers for the current test. Called by the Executor before each test.
    /// </summary>
    /// <param name="parentInstance">The test codeunit instance</param>
    /// <param name="parentType">The test codeunit type</param>
    /// <param name="handlerNames">Comma-separated handler method names from [NavTest].Handlers</param>
    public static void RegisterHandlers(object parentInstance, Type parentType, string? handlerNames)
    {
        Reset();
        if (string.IsNullOrWhiteSpace(handlerNames))
            return;

        _parentInstance = parentInstance;

        var names = handlerNames.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var name in names)
        {
            // Find the method on the parent type
            var method = parentType.GetMethod(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (method == null) continue;

            // Check for [NavHandler] attribute to determine handler type
            var handlerAttr = method.GetCustomAttributes()
                .FirstOrDefault(a => a.GetType().Name == "NavHandlerAttribute");
            if (handlerAttr != null)
            {
                // Read the HandlerType property (NavHandlerType enum)
                var handlerTypeProp = handlerAttr.GetType().GetProperty("HandlerType");
                if (handlerTypeProp != null)
                {
                    var handlerType = handlerTypeProp.GetValue(handlerAttr);
                    var handlerTypeName = handlerType?.ToString() ?? "";

                    if (handlerTypeName == "Confirm")
                        _confirmHandler = method;
                    else if (handlerTypeName == "Message")
                        _messageHandler = method;
                    else if (handlerTypeName == "ModalPage")
                        _modalPageHandler = method;
                    else if (handlerTypeName == "RequestPage" || handlerTypeName == "Page")
                        _requestPageHandler = method;
                    else if (handlerTypeName == "Report")
                        _reportHandler = method;
                    else if (handlerTypeName == "SendNotification")
                        _sendNotificationHandler = method;
                }
            }
        }
    }

    /// <summary>
    /// Invoke the registered confirm handler, if any.
    /// Returns (true, reply) if a handler was found and invoked.
    /// Returns (false, default) if no handler is registered.
    /// </summary>
    public static (bool Handled, bool Reply) InvokeConfirmHandler(string question)
    {
        if (_confirmHandler == null || _parentInstance == null)
            return (false, false);

        // The handler signature is: ConfirmYesHandler(NavText question, ByRef<bool> reply)
        // ByRef<T> is a delegate-based wrapper with getter/setter fields. Default construction
        // leaves the delegates null, causing NullReferenceException. We wire them to local storage.
        var parameters = _confirmHandler.GetParameters();
        if (parameters.Length < 2)
            return (false, false);

        var byRefType = parameters[1].ParameterType;
        var byRef = Activator.CreateInstance(byRefType)!;

        // Create a backing store: bool[] with one element
        var storage = new bool[] { false };

        // Find the setter and getter delegate fields and wire them to our storage
        foreach (var field in byRefType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            var ft = field.FieldType;
            // Setter: Action<bool> or equivalent delegate
            if (ft.Name.Contains("Action") || (field.Name.Contains("set") || field.Name.Contains("Set")))
            {
                try
                {
                    Action<bool> setter = v => storage[0] = v;
                    field.SetValue(byRef, Delegate.CreateDelegate(ft, setter.Target!, setter.Method));
                }
                catch { /* try next field */ }
            }
            // Getter: Func<bool> or equivalent delegate
            if (ft.Name.Contains("Func") || (field.Name.Contains("get") || field.Name.Contains("Get")))
            {
                try
                {
                    Func<bool> getter = () => storage[0];
                    field.SetValue(byRef, Delegate.CreateDelegate(ft, getter.Target!, getter.Method));
                }
                catch { /* try next field */ }
            }
        }

        try
        {
            _confirmHandler.Invoke(_parentInstance, new object[] { new NavText(question), byRef });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }

        return (true, storage[0]);
    }

    /// <summary>
    /// Invoke the registered message handler, if any.
    /// Returns true if a handler was found and invoked.
    /// </summary>
    public static bool InvokeMessageHandler(string message)
    {
        if (_messageHandler == null || _parentInstance == null)
            return false;

        try
        {
            _messageHandler.Invoke(_parentInstance, new object[] { new NavText(message) });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }

        return true;
    }

    /// <summary>
    /// Invoke the registered modal page handler for the given page ID.
    /// Creates a MockTestPageHandle, passes it to the handler, and returns
    /// the FormResult set by the handler (via OK/Cancel action invocation).
    /// Throws if no handler is registered.
    /// </summary>
    public static FormResult InvokeModalPageHandler(int pageId)
    {
        if (_modalPageHandler == null || _parentInstance == null)
            throw new Exception($"No ModalPageHandler registered for page {pageId}. " +
                "Add [HandlerFunctions('YourHandler')] to the test and a " +
                "[ModalPageHandler] procedure.");

        // Create a TestPage handle for the modal page
        var testPage = new MockTestPageHandle(pageId);

        try
        {
            _modalPageHandler.Invoke(_parentInstance, new object[] { testPage });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }

        return testPage.ModalResult;
    }

    /// <summary>
    /// Invoke the registered request page handler for the given report/page ID.
    /// Falls back to the modal page handler because BC represents both through
    /// TestPage-like handler parameters after rewriting.
    /// </summary>
    public static void InvokeRequestPageHandler(int pageId)
    {
        var handler = _requestPageHandler ?? _modalPageHandler;
        if (handler == null || _parentInstance == null)
            throw new Exception($"No RequestPageHandler registered for report/page {pageId}. " +
                "Add [HandlerFunctions('YourHandler')] to the test and a " +
                "[RequestPageHandler] or [ModalPageHandler] procedure.");

        var testPage = new MockTestPageHandle(pageId);

        try
        {
            handler.Invoke(_parentInstance, new object[] { testPage });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }

    /// <summary>
    /// Invoke the registered request page handler if one is registered; returns false if none.
    /// Used by MockReportHandle.Run/RunModal to show the request page before report execution.
    /// Does NOT throw when no handler is registered — caller continues silently.
    /// </summary>
    public static bool TryInvokeRequestPageHandler(int pageId)
    {
        var handler = _requestPageHandler ?? _modalPageHandler;
        if (handler == null || _parentInstance == null)
            return false;

        var testPage = new MockTestPageHandle(pageId);

        try
        {
            handler.Invoke(_parentInstance, new object[] { testPage });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }

        return true;
    }

    /// <summary>
    /// Invoke the registered report handler for the given report ID.
    /// Creates a MockTestPageHandle, passes it to the handler, and returns.
    /// If no handler is registered, silently returns (Report.Run without handler is valid).
    /// </summary>
    public static bool InvokeReportHandler(int reportId)
    {
        if (_reportHandler == null || _parentInstance == null)
            return false;

        var testPage = new MockTestPageHandle(reportId);

        try
        {
            _reportHandler.Invoke(_parentInstance, new object[] { testPage });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }

        return true;
    }

    /// <summary>
    /// Check if a report handler is registered.
    /// </summary>
    public static bool HasReportHandler => _reportHandler != null;

    /// <summary>
    /// Check if a confirm handler is registered.
    /// </summary>
    public static bool HasConfirmHandler => _confirmHandler != null;

    /// <summary>
    /// Check if a message handler is registered.
    /// </summary>
    public static bool HasMessageHandler => _messageHandler != null;

    /// <summary>
    /// Check if a modal page handler is registered.
    /// </summary>
    public static bool HasModalPageHandler => _modalPageHandler != null;

    /// <summary>
    /// Check if a send notification handler is registered.
    /// </summary>
    public static bool HasSendNotificationHandler => _sendNotificationHandler != null;

    /// <summary>
    /// Invoke the registered send notification handler, if any.
    /// The handler signature is: bool Handler(ByRef&lt;MockNotification&gt; notification)
    /// Returns true if a handler was found and invoked.
    /// </summary>
    public static bool InvokeSendNotificationHandler(MockNotification notification)
    {
        if (_sendNotificationHandler == null || _parentInstance == null)
            return false;

        // The handler takes ByRef<MockNotification>. Build one with getter/setter delegates.
        var parameters = _sendNotificationHandler.GetParameters();
        if (parameters.Length < 1)
            return false;

        var byRefType = parameters[0].ParameterType;
        var byRef = Activator.CreateInstance(byRefType)!;

        var storage = new MockNotification[] { notification };

        foreach (var field in byRefType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance))
        {
            var ft = field.FieldType;
            // Setter delegate
            if (ft.Name.Contains("Action") || field.Name.Contains("set") || field.Name.Contains("Set"))
            {
                try
                {
                    Action<MockNotification> setter = v => storage[0] = v;
                    field.SetValue(byRef, Delegate.CreateDelegate(ft, setter.Target!, setter.Method));
                }
                catch { /* try next field */ }
            }
            // Getter delegate
            if (ft.Name.Contains("Func") || field.Name.Contains("get") || field.Name.Contains("Get"))
            {
                try
                {
                    Func<MockNotification> getter = () => storage[0];
                    field.SetValue(byRef, Delegate.CreateDelegate(ft, getter.Target!, getter.Method));
                }
                catch { /* try next field */ }
            }
        }

        try
        {
            _sendNotificationHandler.Invoke(_parentInstance, new object[] { byRef });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }

        return true;
    }

    /// <summary>
    /// Reset all registered handlers. Called between tests.
    /// </summary>
    public static void Reset()
    {
        _parentInstance = null;
        _confirmHandler = null;
        _messageHandler = null;
        _modalPageHandler = null;
        _requestPageHandler = null;
        _reportHandler = null;
        _sendNotificationHandler = null;
    }
}
