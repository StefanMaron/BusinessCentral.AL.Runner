// Isolated events (AL `[IntegrationEvent(IncludeSender, GlobalVarAccess, Isolated = true)]`) for
// the runner's own event dispatcher. See CodeunitEventDispatcher.cs for the dispatch itself.
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner;

public static partial class BcRuntime
{
    private static readonly ConditionalWeakTable<Type, StrongBox<bool>> _isolatedEventScopes = new();

    /// <summary>
    /// Whether the event a publisher scope belongs to is declared <c>Isolated = true</c>. BC reads
    /// the same thing, <c>NavEventAttribute.Isolated</c> on the publisher method, into
    /// <c>NavEventSubscription.OriginalEventIsIsolated</c>.
    /// </summary>
    internal static bool IsIsolatedEventScope(Type scopeType, string eventMethodName)
        => _isolatedEventScopes.GetValue(scopeType, t => new StrongBox<bool>(
            ReadIsolated(t.DeclaringType, eventMethodName))).Value;

    private static bool ReadIsolated(Type? publisherType, string eventMethodName)
    {
        if (publisherType == null) return false;
        foreach (var m in publisherType.GetMethods(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static
                     | BindingFlags.DeclaredOnly))
        {
            if (!string.Equals(m.Name, eventMethodName, StringComparison.Ordinal)) continue;
            if (m.GetCustomAttribute<NavEventAttribute>(inherit: false) is { } attr)
                return attr.Isolated;
        }
        return false;
    }

    private static void InvokeSubscriberForEvent(bool isolated, object publisherScope, Type scopeType,
        object treeObj, MethodInfo subscriberMethod, object? boundInstance)
    {
        if (isolated)
            InvokeIsolatedEventSubscriber(
                () => InvokeOneSubscriber(publisherScope, scopeType, treeObj, subscriberMethod, boundInstance));
        else
            InvokeOneSubscriber(publisherScope, scopeType, treeObj, subscriberMethod, boundInstance);
    }

    /// <summary>
    /// Invoke one subscriber of an isolated event the way BC's
    /// <c>NavEventScope.CallEventSubscriberOnALIsolatedEventAsync</c> does (decompiled Ncl
    /// 28.4.53241): when the session holds no write transaction and is not installing or
    /// upgrading, the subscriber runs inside BC's own
    /// <c>BeginTransactionWorldAndTransaction</c> / <c>EndTransactionWorldAndTransaction(result)</c>
    /// pair, and a <see cref="NavBaseException"/> it raises is caught and not rethrown, with
    /// <c>result == false</c> so the world rolls its writes back. Otherwise it runs plainly and the
    /// error propagates. Corpus 67103 "Test Isolated Event Sub Error" pins all four shapes (#4721).
    ///
    /// <para>Observably equivalent: the Begin/End pair and <c>HasWriteTransaction</c> are BC's own
    /// members, carrying the runner's rollback-scope prepends (#4643). What is not reproduced is
    /// BC's reporting of the trapped error — <c>NavEventSubscription.ReportErrors</c> over the
    /// subscription's error reporters, and a telemetry tag — neither of which AL can observe — and
    /// BC's <c>FlushBufferedWritesAsync</c> inside the try, which is a no-op here because the
    /// runner's in-memory provider never buffers writes.</para>
    ///
    /// <para>Trap: the runner's own refusals (<see cref="Infrastructure.BcShapeGapException"/>,
    /// <see cref="Infrastructure.BcAppSymbolReadException"/>) can arrive wrapped in a
    /// <see cref="NavBaseException"/> and must never be trapped here; a
    /// <c>RunnerOutOfScopeException</c> is not a NavBaseException and propagates too.</para>
    /// </summary>
    internal static void InvokeIsolatedEventSubscriber(Action invoke)
    {
        if (SkeletonSession is not NavSession session)
            throw new Infrastructure.BcShapeGapException(
                "isolated event subscriber", "BcRuntime.SkeletonSession",
                "no NavSession to open the isolated transaction world on; running the subscriber un-isolated would let its error and writes escape (#4721)");
        if (session.AppInstallationContext != null
            || session.AppUpgradeContext != null
            || session.HasWriteTransaction())
        {
            invoke();
            return;
        }

        bool result = false;
        try
        {
            session.BeginTransactionWorldAndTransaction();
            invoke();
            result = true;
        }
        catch (Exception ex) when (IsTrappedByIsolatedEvent(ex, session.CancellationToken.IsCancellationRequested))
        {
        }
        finally
        {
            session.EndTransactionWorldAndTransaction(result);
        }
    }

    internal static bool IsTrappedByIsolatedEvent(Exception ex, bool sessionCancelled)
    {
        var inner = ex is TargetInvocationException { InnerException: { } ie } ? ie : ex;
        if (Infrastructure.BcShapeGapException.Find(inner) != null) return false;
        if (Infrastructure.BcAppSymbolReadException.Find(inner) != null) return false;
        return inner is NavBaseException && !sessionCancelled;
    }
}
