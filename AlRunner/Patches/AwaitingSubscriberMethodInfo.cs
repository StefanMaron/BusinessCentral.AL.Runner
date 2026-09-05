// AwaitingSubscriberMethodInfo — makes BC's reflection dispatch of a table-event subscriber
// observe an ASYNC subscriber body (issue #2932).
//
// BC's NavEventScope.CallEventSubscriberInternalAsync has two branches:
//
//     subscriber.BumpNoOfCalls();
//     if (subscriber.MemberId == 0)
//         subscriber.SubscriberMethodInfo.Invoke(subscriberInstance, parameters);  // result DISCARDED
//     else if (subscriberInstance.__IsAsync)
//         await subscriberInstance.InvokeAsync(subscriber.MemberId, parameters);
//     else
//         subscriberInstance.Invoke(subscriber.MemberId, parameters);
//
// EventSubscriberPatches.BuildSubscription constructs every NavEventSubscription with
// `memberId: 0` (the runner has no BC member-id table to draw a real id from), so every
// injected table-event subscriber takes the first branch — which throws away the return
// value. That is correct only for a `void` subscriber.
//
// The AL compiler emits an async state machine returning ValueTask whenever the subscriber
// body needs one, and every Base Application / System Application subscriber measured on
// BC 28.1 does (Codeunit9002.CheckCurrentUserCanModifyUser,
// Codeunit418.ValidateLicenseTypeOnAfterInsertUser,
// Codeunit153.CheckSuperPermissionsBeforeModifyUser — all `ret=ValueTask`). Invoking one and
// dropping the ValueTask starts the body and then abandons it: nothing after the first
// suspension runs, and an Error() raised inside is captured by the state machine instead of
// propagating. The write the subscriber existed to refuse went through with no complaint.
//
// Source-compiled subscribers in a test bundle are usually emitted `void`, which is why the
// defect looked like "precompiled subscribers never execute" — they did execute, BC's own
// NoOfCalls counter proves it, but only up to their first await.
//
// Wrapping the raw MethodInfo is enough because NavEventSubscription stores
// NavEventSubscriberMethodInfo.RawMethodInfo verbatim and invokes it directly; nothing in
// BC's dispatch path needs the concrete RuntimeMethodInfo. This is the same correction
// CodeunitEventDispatcher already applies on the codeunit-event path via
// BcRuntime.ObserveAsyncResult — that path awaits, this one did not, and the two dispatch
// paths must maintain the same invariant.
using System.Globalization;
using System.Reflection;

namespace AlRunner.Patches;

/// <summary>
/// A <see cref="MethodInfo"/> decorator that forwards every member to the wrapped method and,
/// on <see cref="Invoke(object,BindingFlags,Binder,object[],CultureInfo)"/>, additionally
/// observes a returned <see cref="System.Threading.Tasks.Task"/> /
/// <see cref="System.Threading.Tasks.ValueTask"/> before returning, so an exception raised
/// inside an async AL subscriber body reaches the caller instead of being swallowed with the
/// discarded task.
/// </summary>
internal sealed class AwaitingSubscriberMethodInfo : MethodInfo
{
    private readonly MethodInfo _inner;

    private AwaitingSubscriberMethodInfo(MethodInfo inner) => _inner = inner;

    /// <summary>The method this decorator forwards to.</summary>
    public MethodInfo Inner => _inner;

    /// <summary>
    /// True when <paramref name="method"/>'s return type is awaitable, i.e. when invoking it
    /// by reflection and discarding the result would abandon the body. `void` subscribers —
    /// the common shape for source-compiled AL in a test bundle — need no wrapper and get
    /// none, so the overwhelmingly common path stays byte-identical to before.
    /// </summary>
    public static bool NeedsAwaiting(MethodInfo method)
    {
        var rt = method.ReturnType;
        if (rt == typeof(void)) return false;
        if (rt == typeof(System.Threading.Tasks.Task)
            || rt == typeof(System.Threading.Tasks.ValueTask)) return true;
        if (!rt.IsGenericType) return false;
        var def = rt.GetGenericTypeDefinition();
        return def == typeof(System.Threading.Tasks.Task<>)
            || def == typeof(System.Threading.Tasks.ValueTask<>);
    }

    /// <summary>
    /// Wrap <paramref name="method"/> when — and only when — its result must be observed.
    /// Returns the original instance otherwise.
    /// </summary>
    public static MethodInfo WrapIfAwaitable(MethodInfo method)
        => NeedsAwaiting(method) ? new AwaitingSubscriberMethodInfo(method) : method;

    public override object? Invoke(object? obj, BindingFlags invokeAttr, Binder? binder,
        object?[]? parameters, CultureInfo? culture)
    {
        var result = _inner.Invoke(obj, invokeAttr, binder, parameters, culture);
        try
        {
            BcRuntime.ObserveAsyncResult(result, _inner);
        }
        catch (Exception ex)
        {
            // BC's caller expects the reflection contract: an exception raised by the invoked
            // body arrives wrapped in TargetInvocationException, which
            // CallEventSubscriberInternalAsync unwraps with ExceptionDispatchInfo and rethrows
            // (rethrowing a NavBaseException verbatim, telemetry-tagging anything else).
            // ObserveAsyncResult rethrows the ORIGINAL exception, so re-wrap it here to keep
            // async and sync subscriber bodies indistinguishable to BC.
            throw new TargetInvocationException(ex);
        }
        return result;
    }

    public override MethodInfo GetBaseDefinition() => _inner.GetBaseDefinition();
    public override ICustomAttributeProvider ReturnTypeCustomAttributes => _inner.ReturnTypeCustomAttributes;
    public override Type ReturnType => _inner.ReturnType;
    public override ParameterInfo ReturnParameter => _inner.ReturnParameter;
    public override MethodAttributes Attributes => _inner.Attributes;
    public override RuntimeMethodHandle MethodHandle => _inner.MethodHandle;
    public override Type? DeclaringType => _inner.DeclaringType;
    public override string Name => _inner.Name;
    public override Type? ReflectedType => _inner.ReflectedType;
    public override Module Module => _inner.Module;
    public override int MetadataToken => _inner.MetadataToken;
    public override CallingConventions CallingConvention => _inner.CallingConvention;
    public override bool IsGenericMethod => _inner.IsGenericMethod;
    public override bool IsGenericMethodDefinition => _inner.IsGenericMethodDefinition;
    public override bool ContainsGenericParameters => _inner.ContainsGenericParameters;
    public override Type[] GetGenericArguments() => _inner.GetGenericArguments();
    public override MethodInfo GetGenericMethodDefinition() => _inner.GetGenericMethodDefinition();
    public override object[] GetCustomAttributes(bool inherit) => _inner.GetCustomAttributes(inherit);
    public override object[] GetCustomAttributes(Type attributeType, bool inherit)
        => _inner.GetCustomAttributes(attributeType, inherit);
    public override IList<CustomAttributeData> GetCustomAttributesData() => _inner.GetCustomAttributesData();
    public override bool IsDefined(Type attributeType, bool inherit) => _inner.IsDefined(attributeType, inherit);
    public override MethodImplAttributes GetMethodImplementationFlags() => _inner.GetMethodImplementationFlags();
    public override ParameterInfo[] GetParameters() => _inner.GetParameters();
    public override MethodBody? GetMethodBody() => _inner.GetMethodBody();
    public override string ToString() => _inner.ToString() ?? base.ToString()!;
}
