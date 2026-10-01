// Part of NclCecilRewrite (see NclCecilRewrite.cs for the driver + shared helpers).

using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AlRunner.Infrastructure;

public static partial class NclCecilRewrite
{
    private const string ExternalBusinessEventScopeType =
        "Microsoft.Dynamics.Nav.Runtime.NavExternalBusinessEventMethodScope`1";

    /// <summary>
    /// Raising an <c>[ExternalBusinessEvent]</c> completes and delivers nothing (#5149).
    /// <c>NavExternalBusinessEventMethodScope&lt;T&gt;.OnRunExternalEvent</c> is cut back to its
    /// first statement, <c>StmtHit(0)</c>, so the subscription lookup — which opens a SQL
    /// connection and died with a NullReferenceException in
    /// <c>NavSqlConnectionScope.TryOpenConnection</c> — and the log-entry/delivery-task
    /// writes behind it never run. Everything else in <c>RunExternalBusinessEvent</c> (caller
    /// check, exception remapping, scope exit) stays BC's own.
    /// </summary>
    // Observably equivalent: on a BC CI container no external business event subscription can
    // exist (subscriptions are a Dataverse construct), so BC's own body takes its
    // "no matching subscriptions" early return; this rewrite is that return. Citation: the
    // account holder's decision on #5149 (issuecomment-5930043993) and
    // docs/scope.md#external-business-events. Not refused by name: nothing is delivered
    // synchronously on BC, so the AL caller cannot observe the suppression.
    // Trap: OnRunExternalEvent's body was identical on bc270 and bc284 (compare_symbols);
    // re-check if the shape assertion below fires on a new BC build.
    private static void RewriteNcl_ExternalEvents(AssemblyDefinition asm)
    {
        var scopeType = asm.MainModule.GetType(ExternalBusinessEventScopeType)
            ?? throw new InvalidOperationException(
                $"[Cecil] {ExternalBusinessEventScopeType} not found in Ncl — BC's external business "
                + "event shape changed; raising one would reach NavSqlConnectionScope again (#5149).");

        var targets = scopeType.Methods
            .Where(m => m.Name == "OnRunExternalEvent" && m.HasBody && !m.IsStatic
                        && m.Parameters.Count == 1 && m.ReturnType.FullName == "System.Void")
            .ToList();
        if (targets.Count != 1)
            throw new InvalidOperationException(
                $"[Cecil] Expected exactly ONE {ExternalBusinessEventScopeType}.OnRunExternalEvent(owningApp) "
                + $"to rewrite, found {targets.Count}. Raising an external business event would reach "
                + "NavSqlConnectionScope again (#5149).");

        var target = targets[0];
        var stmtHit = target.Body.Instructions
            .FirstOrDefault(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                                 && i.Operand is MethodReference { Name: "StmtHit", Parameters.Count: 1 } r
                                 && r.ReturnType.FullName == "System.Void")
            ?? throw new InvalidOperationException(
                $"[Cecil] {ExternalBusinessEventScopeType}.OnRunExternalEvent no longer calls StmtHit(int); "
                + "re-read its body before rewriting it (#5149).");

        var body = target.Body;
        body.Instructions.Clear();
        body.Variables.Clear();
        body.ExceptionHandlers.Clear();
        body.InitLocals = false;
        var il = body.GetILProcessor();
        il.Append(il.Create(OpCodes.Ldarg_0));
        il.Append(il.Create(OpCodes.Ldc_I4_0));
        il.Append(il.Create(stmtHit.OpCode, (MethodReference)stmtHit.Operand));
        il.Append(il.Create(OpCodes.Ret));
        body.MaxStackSize = 2;
        Console.Error.WriteLine(
            "[Cecil] Rewrote NavExternalBusinessEventMethodScope<T>.OnRunExternalEvent → StmtHit(0); return (no subscription, #5149)");
    }

    private static void AddExternalEventsOwned(HashSet<string> set)
    {
        set.Add(ExternalBusinessEventScopeType + "::OnRunExternalEvent/1");
    }
}
