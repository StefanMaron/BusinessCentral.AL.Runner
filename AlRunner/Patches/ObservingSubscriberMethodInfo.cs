// ObservingSubscriberMethodInfo — makes an AL event subscriber's returned Task/ValueTask
// OBSERVED on the table-event dispatch path, which BC itself does not do for the
// subscriptions this runner injects.
//
// Why this type has to exist at all (#2932):
//
//   Table-event subscribers (Table publisher, ordinals 1-10) are not dispatched by the
//   runner. EventSubscriberPatches builds real NavEventSubscription objects and appends them
//   to BC's own NavEventScope.registeredSubscriptions, and BC's
//   NavEventScope.CallEventSubscriberInternalAsync does the invoking:
//
//       if (subscriber.MemberId == 0)
//           subscriber.SubscriberMethodInfo.Invoke(subscriberInstance, parameters);   // (a)
//       else if (subscriberInstance.__IsAsync)
//           await subscriberInstance.InvokeAsync(subscriber.MemberId, parameters);    // (b)
//       else
//           subscriberInstance.Invoke(subscriber.MemberId, parameters);               // (c)
//
//   Branch (a) DISCARDS the return value. The runner passes memberId 0 when it constructs a
//   NavEventSubscription (it has no BC member-id table for a scanned [NavEventSubscriber]
//   method), so every injected table-event subscriber takes branch (a) — never the awaited
//   branch (b) real BC uses.
//
//   That is harmless for a subscriber the runner compiled itself: the AL-to-C# emitter emits
//   those as synchronous `void` methods, so an AL Error() propagates straight out of
//   MethodInfo.Invoke as a TargetInvocationException and reaches the AL caller.
//
//   It is NOT harmless for a subscriber inside a precompiled app. Microsoft's AL compiler
//   emits Base/System Application subscribers as `async ValueTask` state machines, and an
//   exception thrown inside one is captured onto the returned ValueTask instead of being
//   thrown. Discarding that ValueTask discards the error: the write the subscriber existed to
//   refuse goes through, nothing is logged, and a test that real BC would have failed passes.
//
//   Measured on the al-language corpus before this type existed: 277 of the 295 injected
//   table-event subscriptions (94%) return ValueTask, 470 invocations per corpus run took
//   branch (a) with a ValueTask result, and 11 real AL errors — Base App retention-policy
//   "not in the list of allowed tables" checks among them — were silently swallowed.
//
// The same defect on the CODEUNIT-event path was already fixed: the runner dispatches those
// itself and calls BcRuntime.ObserveAsyncResult on the result (CodeunitEventDispatcher).
// This type is what lets the table-event path reuse that one definition of "observe the
// result" rather than growing a second one — see .claude/rules/loud-failures.md.
using System;
using System.Globalization;
using System.Reflection;

namespace AlRunner.Patches;

/// <summary>
/// A <see cref="MethodInfo"/> that delegates everything to an inner method and additionally
/// completes (and rethrows from) a returned <c>Task</c>/<c>ValueTask</c> before returning.
/// Handed to BC's <c>NavEventSubscriberMethodInfo</c> ctor by
/// <c>EventSubscriberPatches.BuildSubscription</c> so BC's un-awaited
/// <c>SubscriberMethodInfo.Invoke</c> call site cannot drop an AL error.
/// </summary>
internal sealed class ObservingSubscriberMethodInfo : MethodInfo
{
    private readonly MethodInfo _inner;

    internal ObservingSubscriberMethodInfo(MethodInfo inner)
        => _inner = inner ?? throw new ArgumentNullException(nameof(inner));

    /// <summary>The subscriber method actually invoked — the one the registry scanned.</summary>
    internal MethodInfo Inner => _inner;

    public override object? Invoke(object? obj, BindingFlags invokeAttr, Binder? binder,
        object?[]? parameters, CultureInfo? culture)
    {
        var result = _inner.Invoke(obj, invokeAttr, binder, parameters, culture);
        try
        {
            // Blocking is correct here for the same reason it is in the codeunit-event
            // dispatcher: the runner drives AL synchronously against an in-memory provider,
            // so these tasks are already complete or complete inline.
            BcRuntime.ObserveAsyncResult(result, _inner);
        }
        catch (TargetInvocationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // BC's CallEventSubscriberInternalAsync has a dedicated TargetInvocationException
            // arm that rethrows the inner exception through ExceptionDispatchInfo — the arm a
            // SYNCHRONOUS subscriber's exception already takes, because MethodInfo.Invoke
            // wraps it. Present the async-captured exception identically so both AL compile
            // shapes reach the AL caller by the same route, with the same stack handling.
            throw new TargetInvocationException(ex);
        }
        return result;
    }

    // --- everything below is straight delegation; the decorator must be indistinguishable
    // --- from the inner method to BC's reflection over it (parameter binding in
    // --- NavEventSubscription.FindTriggerParameters, the [NavEventSubscriber] attribute read
    // --- by NavEventSubscriberMethodInfo, DeclaringType checks in NavEventScope).
    public override ICustomAttributeProvider ReturnTypeCustomAttributes => _inner.ReturnTypeCustomAttributes;
    public override MethodAttributes Attributes => _inner.Attributes;
    public override RuntimeMethodHandle MethodHandle => _inner.MethodHandle;
    public override Type? DeclaringType => _inner.DeclaringType;
    public override string Name => _inner.Name;
    public override Type? ReflectedType => _inner.ReflectedType;
    public override Type ReturnType => _inner.ReturnType;
    public override ParameterInfo ReturnParameter => _inner.ReturnParameter;
    public override CallingConventions CallingConvention => _inner.CallingConvention;
    public override bool IsGenericMethod => _inner.IsGenericMethod;
    public override bool IsGenericMethodDefinition => _inner.IsGenericMethodDefinition;
    public override bool ContainsGenericParameters => _inner.ContainsGenericParameters;
    public override MemberTypes MemberType => _inner.MemberType;
    public override int MetadataToken => _inner.MetadataToken;
    public override Module Module => _inner.Module;
    public override MethodInfo GetBaseDefinition() => _inner.GetBaseDefinition();
    public override object[] GetCustomAttributes(bool inherit) => _inner.GetCustomAttributes(inherit);
    public override object[] GetCustomAttributes(Type attributeType, bool inherit)
        => _inner.GetCustomAttributes(attributeType, inherit);
    public override System.Collections.Generic.IList<CustomAttributeData> GetCustomAttributesData()
        => _inner.GetCustomAttributesData();
    public override MethodImplAttributes GetMethodImplementationFlags() => _inner.GetMethodImplementationFlags();
    public override ParameterInfo[] GetParameters() => _inner.GetParameters();
    public override bool IsDefined(Type attributeType, bool inherit) => _inner.IsDefined(attributeType, inherit);
    public override Type[] GetGenericArguments() => _inner.GetGenericArguments();
    public override MethodInfo GetGenericMethodDefinition() => _inner.GetGenericMethodDefinition();
    public override MethodBody? GetMethodBody() => _inner.GetMethodBody();
    public override string ToString() => _inner.ToString()!;
    /// <summary>
    /// DELIBERATELY ASYMMETRIC, and safe only because nothing compares this object (#3115).
    ///
    /// <c>decorator.Equals(inner)</c> is <c>true</c> — the decorator answers as the method it
    /// wraps, which is the point. <c>inner.Equals(decorator)</c> is <c>false</c>: the inner
    /// <c>RuntimeMethodInfo</c> knows nothing about this type and there is no way to teach it.
    /// <see cref="GetHashCode"/> returns the inner method's hash either way, so a
    /// <c>HashSet</c>/<c>Dictionary</c> holding one of the two and probed with the other hits
    /// the same bucket and then answers differently depending on WHICH side was stored — which
    /// side a given collection calls <c>Equals</c> on is a BCL implementation detail, so the
    /// only safe reading is that membership is not well-defined for a mixed collection.
    ///
    /// Why that is harmless today — measured, not assumed:
    ///
    ///  * On the runner side, <c>EventSubscriberPatches._injectedSubscriberMethods</c> is keyed
    ///    on the inner <c>sub.Method</c> and this decorator is never added to it. It is
    ///    constructed in <c>BuildSubscription</c> and handed straight to BC.
    ///  * It DOES outlive that call — BC's <c>NavEventSubscription</c> ctor stores
    ///    <c>subscriberMethodInfo.RawMethodInfo</c> and re-exposes it as the public
    ///    <c>SubscriberMethodInfo</c> property — but a whole-assembly scan of
    ///    <c>Microsoft.Dynamics.Nav.Ncl.dll</c> 28.1.49838.53910 finds only three uses of that
    ///    property: <c>.DeclaringType</c>, <c>.Invoke(...)</c>, and the
    ///    <c>EventSubscriberDiagnosticMessage</c> helpers reading <c>Name</c>/<c>GetParameters</c>/
    ///    <c>ReturnType</c>. No equality comparison, and no collection keyed on it —
    ///    <c>NavEventScope.RemoveSubscribersFromAppGroup</c> removes by
    ///    <c>SubscriberNavAppGroup.GroupId</c>, not by method identity.
    ///
    /// So: do NOT "fix" the asymmetry on sight. Making it symmetric would mean changing what
    /// the decorator claims to be equal to, across every BC reflection path that receives it,
    /// for a comparison no caller performs. If a caller ever does start comparing it, THAT is
    /// the change to reason about, and <c>EqualsIsAsymmetric_ByDesignAndPinnedHere</c> in
    /// <c>AlRunner.Tests/ObservingSubscriberMethodInfoTests.cs</c> is the test that will make
    /// it a deliberate decision rather than a silent one.
    /// </summary>
    public override bool Equals(object? obj)
        => obj is ObservingSubscriberMethodInfo o ? _inner.Equals(o._inner) : _inner.Equals(obj);
    public override int GetHashCode() => _inner.GetHashCode();
}
