namespace AlRunner.Runtime;

/// <summary>
/// Lightweight replacement for NavInterfaceHandle.
/// In the BC runtime, NavInterfaceHandle wraps an ITreeObject to represent
/// AL interface references. For standalone execution, we just store the object.
/// </summary>
public class MockInterfaceHandle
{
    private object? _implementation;

    public MockInterfaceHandle()
    {
    }

    /// <summary>
    /// Assigns an interface implementation (codeunit) to this handle.
    /// In BC, ALAssign wraps the codeunit as an interface implementation.
    /// </summary>
    public void ALAssign(object? implementation)
    {
        _implementation = implementation;
    }

    public void Clear()
    {
        _implementation = null;
    }

    /// <summary>
    /// Invoke a method on the interface implementation by member ID.
    /// Similar to MockCodeunitHandle.Invoke but via the interface dispatch pattern.
    /// In BC, InvokeInterfaceMethod dispatches through the codeunit's IsInterfaceMethod table.
    /// </summary>
    public object? InvokeInterfaceMethod(int memberId, object[] args)
    {
        if (_implementation == null)
            throw new InvalidOperationException("Interface not assigned");

        // If the implementation is a MockCodeunitHandle, delegate to it
        if (_implementation is MockCodeunitHandle handle)
            return handle.Invoke(memberId, args);

        throw new NotSupportedException(
            $"Interface dispatch not supported for implementation type {_implementation.GetType().Name}");
    }

    /// <summary>
    /// 3-arg overload: InvokeInterfaceMethod(interfaceId, memberId, args)
    /// The interfaceId identifies which interface is being called (ignored in standalone mode).
    /// </summary>
    public object? InvokeInterfaceMethod(int interfaceId, int memberId, object[] args)
    {
        return InvokeInterfaceMethod(memberId, args);
    }
}
