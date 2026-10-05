// Part of NclCecilRewrite (see NclCecilRewrite.cs for the driver + shared helpers).

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AlRunner.Infrastructure;

public static partial class NclCecilRewrite
{
    private const string SqlConnectionScopeType = "Microsoft.Dynamics.Nav.Runtime.NavSqlConnectionScope";

    /// <summary>
    /// Backstop for every Ncl path that opens a SQL connection on the skeleton (#5190):
    /// <c>NavSqlConnectionScope.TryOpenConnection</c> starts with
    /// <c>SqlConnectionPatches.RefuseWithoutDatabaseServer(database)</c>, which refuses by name when the
    /// database has no database server / SQL connection provider and otherwise falls through to BC's own
    /// body, untouched.
    /// </summary>
    // Observably equivalent: the guard fires only where BC's first statement
    // (`database.DatabaseServer.SqlConnectionProvider.GetConnection(...)`) would already have thrown a
    // NullReferenceException, so it swaps the exception and never an outcome; a database with a provider
    // runs BC's body unchanged. Citation: SqlConnectionPatches.RefuseWithoutDatabaseServer and
    // docs/scope.md#sql-connection. A call is prepended and no BC token is touched (it adds one
    // memberref to the runner assembly, as PrependStaticCall always does).
    // Trap: TryOpenConnection's body is byte-identical in 27.5.46862.53931 and 28.5.54151.55132 Ncl; the
    // shape assertion below is what fires on a build where it is not.
    internal static void RewriteNcl_SqlConnection(AssemblyDefinition asm)
    {
        var scopeType = asm.MainModule.GetType(SqlConnectionScopeType)
            ?? throw new InvalidOperationException(
                $"[Cecil] {SqlConnectionScopeType} not found in Ncl — BC's SQL connection shape changed; "
                + "opening a connection would die with an unnamed NullReferenceException again (#5190).");

        var targets = scopeType.Methods
            .Where(m => m.Name == "TryOpenConnection" && m.HasBody && !m.IsStatic
                        && m.Parameters.Count == 3 && m.ReturnType.FullName == "System.Boolean"
                        && m.Parameters[0].ParameterType.FullName == "Microsoft.Dynamics.Nav.Runtime.NavDatabase")
            .ToList();
        if (targets.Count != 1)
            throw new InvalidOperationException(
                $"[Cecil] Expected exactly ONE {SqlConnectionScopeType}.TryOpenConnection(NavDatabase, "
                + $"TransactionType, NavSqlConnection) to guard, found {targets.Count}. Opening a SQL "
                + "connection would die with an unnamed NullReferenceException again (#5190).");

        // The guard tests the two dereferences BC's body opens with; if BC stops making them the guard
        // would be testing something else, so refuse here rather than guess.
        var target = targets[0];
        foreach (var member in new[] { "get_DatabaseServer", "get_SqlConnectionProvider" })
        {
            if (!target.Body.Instructions.Any(i => (i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
                                                   && i.Operand is MethodReference r && r.Name == member))
                throw new InvalidOperationException(
                    $"[Cecil] {SqlConnectionScopeType}.TryOpenConnection no longer calls {member}; "
                    + "re-read its body before guarding it (#5190).");
        }

        var helper = typeof(AlRunner.Patches.SqlConnectionPatches).GetMethod(
            nameof(AlRunner.Patches.SqlConnectionPatches.RefuseWithoutDatabaseServer),
            BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException(
                "[Cecil] SqlConnectionPatches.RefuseWithoutDatabaseServer not found");

        // Arg slot 1 is `database`: slot 0 is `this`, which no helper needs.
        PrependStaticCallArgs(asm.MainModule, target, helper, 1);
    }

    private static void AddSqlConnectionOwned(HashSet<string> set)
    {
        set.Add(SqlConnectionScopeType + "::TryOpenConnection/3");
    }
}
