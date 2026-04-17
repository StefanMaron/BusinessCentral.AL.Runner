namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;

/// <summary>
/// Lightweight replacement for NavInterfaceHandle.
/// In the BC runtime, NavInterfaceHandle wraps an ITreeObject to represent
/// AL interface references. For standalone execution, we just store the object.
///
/// Implements <see cref="ITreeObject"/> and
/// <see cref="IALAssignable{T}"/> so MockInterfaceHandle can satisfy the
/// type constraints on <c>NavObjectList&lt;T&gt;</c> — BC generates
/// <c>NavObjectList&lt;MockInterfaceHandle&gt;</c> for AL's
/// <c>List of [Interface X]</c>.
/// </summary>
public class MockInterfaceHandle : ITreeObject, IALAssignable<MockInterfaceHandle>
{
    private object? _implementation;

    public MockInterfaceHandle()
    {
    }

    /// <summary>
    /// Constructor accepting a parent scope — used when an interface is returned
    /// from a function. The BC compiler passes the parent NavScope as an argument.
    /// In standalone mode, we ignore the parent.
    /// </summary>
    public MockInterfaceHandle(object? parent)
    {
    }

    // ITreeObject — stub properties, never inspected in standalone mode.
    TreeHandler ITreeObject.Tree => null!;
    TreeObjectType ITreeObject.Type => default;
    bool ITreeObject.SingleThreaded => false;

    /// <summary>
    /// Factory used by the rewriter when translating
    /// <c>ALCompiler.ToInterface(this, codeunit)</c>. Wraps an implementation
    /// (usually a <see cref="MockCodeunitHandle"/>) so it can be stored in a
    /// <c>NavObjectList&lt;MockInterfaceHandle&gt;</c>.
    /// </summary>
    public static MockInterfaceHandle Wrap(object? implementation)
    {
        var h = new MockInterfaceHandle();
        h.ALAssign(implementation);
        return h;
    }

    /// <summary>
    /// IALAssignable&lt;MockInterfaceHandle&gt;.ALAssign — copy another
    /// handle's implementation reference into this one. Used when AL
    /// reassigns interface variables inside a NavObjectList.
    /// </summary>
    public void ALAssign(MockInterfaceHandle other)
    {
        _implementation = other?._implementation;
    }

    /// <summary>
    /// Assigns an interface implementation (codeunit) to this handle.
    /// In BC, ALAssign wraps the codeunit as an interface implementation.
    /// </summary>
    public void ALAssign(object? implementation)
    {
        // Unwrap: if the caller hands us another MockInterfaceHandle,
        // adopt its implementation instead of nesting.
        if (implementation is MockInterfaceHandle inner)
        {
            _implementation = inner._implementation;
            return;
        }
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

        // If the implementation is another MockInterfaceHandle (e.g., returned from a factory),
        // delegate through it
        if (_implementation is MockInterfaceHandle innerHandle)
            return innerHandle.InvokeInterfaceMethod(memberId, args);

        // AL pattern: `Flag := Strategy;` where Strategy is an enum with
        // `Implementation = "Iface" = "Codeunit"`. BC assigns the NavOption
        // to the interface handle; we resolve the implementation by looking
        // up EnumRegistry with (enum metadata id / name, ordinal) and
        // forwarding to the matching codeunit.
        if (_implementation is Microsoft.Dynamics.Nav.Runtime.NavOption navOpt)
        {
            var resolved = ResolveEnumImplementation(navOpt);
            if (resolved != null)
            {
                // Cache so future calls skip the resolution.
                _implementation = resolved;
                return resolved.Invoke(memberId, args);
            }
        }

        throw new NotSupportedException(
            $"Interface dispatch not supported for implementation type {_implementation.GetType().Name}");
    }

    private static MockCodeunitHandle? ResolveEnumImplementation(Microsoft.Dynamics.Nav.Runtime.NavOption navOpt)
    {
        var ordinal = navOpt.Value;
        int? enumId = null;
        string? enumName = null;

        try
        {
            var meta = navOpt.NavOptionMetadata;
            if (meta != null)
            {
                // NCLOptionMetadata exposes Id + Name; neither is guaranteed
                // to be set when we rewrote the underlying Create call, so
                // try both lookup paths.
                var idProp = meta.GetType().GetProperty("Id");
                if (idProp?.GetValue(meta) is int id) enumId = id;
                var nameProp = meta.GetType().GetProperty("Name");
                if (nameProp?.GetValue(meta) is string name) enumName = name;
            }
        }
        catch { /* metadata inspection is best-effort */ }

        string? codeunitName = null;
        if (enumId.HasValue)
            codeunitName = EnumRegistry.GetImplementationCodeunitName(enumId.Value, ordinal);
        if (codeunitName == null && !string.IsNullOrEmpty(enumName))
            codeunitName = EnumRegistry.GetImplementationCodeunitNameByEnumName(enumName, ordinal);

        // Last resort: scan every registered implementation row for one whose
        // ordinal matches. In a BC test this is unambiguous when the scope has
        // a single enum→interface assignment in flight.
        if (codeunitName == null)
            codeunitName = EnumRegistry.FindAnyImplementationCodeunit(ordinal);

        if (codeunitName == null) return null;

        // Resolve via the transpile-time codeunit name → id registry, which
        // is populated by Pipeline.Run from the raw AL source. Much more
        // reliable than trying to reflect over uninitialized generated
        // codeunit classes.
        var cuId = CodeunitNameRegistry.GetIdByName(codeunitName);
        if (cuId.HasValue)
            return MockCodeunitHandle.Create(cuId.Value);

        return null;
    }

    /// <summary>
    /// 3-arg overload: InvokeInterfaceMethod(interfaceId, memberId, args)
    /// The interfaceId identifies which interface is being called (ignored in standalone mode).
    /// </summary>
    public object? InvokeInterfaceMethod(int interfaceId, int memberId, object[] args)
    {
        return InvokeInterfaceMethod(memberId, args);
    }

    /// <summary>
    /// AL <c>myVar is IBar</c> — checks if the underlying implementation supports the given
    /// interface ID. BC generates <c>myVar.IsInterfaceOfType(interfaceId)</c> for this pattern.
    /// Delegates to the BC-generated <c>IsInterfaceOfType(int)</c> method on the codeunit class
    /// (kept by the rewriter; <c>override</c> is stripped so it compiles without the base class).
    /// </summary>
    public bool IsInterfaceOfType(int interfaceId)
    {
        var instance = GetUnderlyingObject();
        if (instance == null) return false;

        var method = instance.GetType().GetMethod(
            "IsInterfaceOfType",
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
            null,
            new[] { typeof(int) },
            null);
        if (method == null) return false;

        return (bool)method.Invoke(instance, new object[] { interfaceId })!;
    }

    /// <summary>
    /// AL <c>myVar as IBar</c> — returns <c>this</c> if the implementation supports the
    /// interface ID, otherwise throws <see cref="InvalidCastException"/>.
    /// BC generates <c>myVar.AsInterfaceOfType(interfaceId)</c> for this pattern.
    /// </summary>
    public MockInterfaceHandle AsInterfaceOfType(int interfaceId)
    {
        if (!IsInterfaceOfType(interfaceId))
            throw new InvalidCastException(
                $"Interface implementation does not support interface with ID {interfaceId}.");
        return this;
    }

    /// <summary>
    /// Unwraps nested handles and codeunit wrappers to reach the codeunit instance
    /// that carries the BC-generated IsInterfaceOfType(int) method.
    /// </summary>
    private object? GetUnderlyingObject()
    {
        if (_implementation is MockCodeunitHandle handle)
            return handle.GetUnderlyingInstance();
        if (_implementation is MockInterfaceHandle inner)
            return inner.GetUnderlyingObject();
        return _implementation;
    }
}
