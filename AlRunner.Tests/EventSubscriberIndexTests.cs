// Issue #2369: the injectors that run on every record construction look subscribers up through
// a per-table index instead of scanning every registered subscriber. The index is derived state,
// so the risk it adds is staleness: a subscriber registered after the index was first built must
// still be found. These tests load a NEW assembly after the first lookup — the shape a --server
// reload, a --watch cycle or a lazily loaded dependency produces — and assert the lookup sees it.
//
// The subscriber assemblies are compiled here with a duck-typed NavEventSubscriberAttribute, the
// same shape ObjectEventSubscriberRegistrationMechanismTests uses: discovery matches the attribute
// by simple type name and reads TargetObjectId / TargetMethodName / TargetFieldName reflectively.
// End-to-end dispatch through a real runner is ServerValidateSubscriberReloadTests.

using System.Reflection;
using AlRunner.Patches;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace AlRunner.Tests;

[Collection(EventSubscriberRegistrySerialCollection.Name)]
public sealed class EventSubscriberIndexTests
{
    // Assemblies never unload, so every subscriber a test loads stays registered for the rest of
    // the process. Each test takes its own table ids so an earlier test's subscribers cannot
    // satisfy a later test's assertion.
    private static int _nextTableId = 79900;

    private static int NextTableId() => Interlocked.Add(ref _nextTableId, 10);

    private static byte[] Compile(string assemblyName, string source)
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var compilation = CSharpCompilation.Create(
            assemblyName,
            new[] { CSharpSyntaxTree.ParseText(source) },
            new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(Path.Combine(runtimeDir, "System.Runtime.dll")),
            },
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);
        Assert.True(result.Success,
            string.Join("; ", result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return ms.ToArray();
    }

    /// <summary>A codeunit-shaped type whose subscribers target <paramref name="table"/> and
    /// <paramref name="otherTable"/>. Declaration order is the order discovery reads.</summary>
    private static string SubscriberSource(int table, int otherTable) => $$"""
        using System;
        namespace Fx.SubscriberIndex
        {
            public sealed class FakeObjectId
            {
                public FakeObjectId(int objectType, int objectNumber) { ObjectType = objectType; ObjectNumber = objectNumber; }
                public int ObjectType { get; }
                public int ObjectNumber { get; }
            }

            public sealed class NavEventSubscriberAttribute : Attribute
            {
                public NavEventSubscriberAttribute(int objectType, int objectId, string methodName, string fieldName)
                {
                    TargetObjectId = new FakeObjectId(objectType, objectId);
                    TargetMethodName = methodName;
                    TargetFieldName = fieldName;
                }
                public object TargetObjectId { get; }
                public string TargetMethodName { get; }
                public string TargetFieldName { get; }
                public int TargetFieldId => 0;
            }

            public class CodeunitSubscriberIndexProbe
            {
                [NavEventSubscriber(1, {{table}}, "OnAfterValidateEvent", "Qty")]
                public void QtyValidated() { }

                [NavEventSubscriber(1, {{otherTable}}, "OnAfterValidateEvent", "Qty")]
                public void OtherTableQtyValidated() { }

                [NavEventSubscriber(1, {{table}}, "OnBeforeValidateEvent", "Note")]
                public void NoteValidating() { }

                [NavEventSubscriber(1, {{table}}, "OnAfterInsertEvent", "")]
                public void Inserted() { }
            }
        }
        """;

    private static Assembly LoadSubscribers(int table, int otherTable)
        => Assembly.Load(Compile($"al-runner-subscriber-index-{Guid.NewGuid():N}", SubscriberSource(table, otherTable)));

    private static string[] Names(IEnumerable<MethodInfo> methods) => methods.Select(m => m.Name).ToArray();

    [Fact]
    public void AssemblyLoadHandler_RunsAfterTheLoadedAssemblyIsEnumerable()
    {
        // EnsureRegistryFresh's gate reads an epoch that AppDomain.AssemblyLoad bumps, then calls
        // GetAssemblies(). That is only complete if a handler never runs BEFORE the new assembly
        // shows up in GetAssemblies() — otherwise a scan could record the epoch and miss the
        // assembly that bumped it. This pins the platform property the gate depends on.
        var name = $"al-runner-load-order-{Guid.NewGuid():N}";
        var bytes = Compile(name, "public class Probe { }");
        bool? enumerableInHandler = null;
        AssemblyLoadEventHandler handler = (_, e) =>
        {
            if (e.LoadedAssembly.GetName().Name == name)
                enumerableInHandler = AppDomain.CurrentDomain.GetAssemblies().Contains(e.LoadedAssembly);
        };
        AppDomain.CurrentDomain.AssemblyLoad += handler;
        try { Assembly.Load(bytes); }
        finally { AppDomain.CurrentDomain.AssemblyLoad -= handler; }

        Assert.True(enumerableInHandler.HasValue, "AssemblyLoad was not raised for Assembly.Load(byte[])");
        Assert.True(enumerableInHandler.Value, "the handler ran before the assembly was in GetAssemblies()");
    }

    [Fact]
    public void SubscribersLoadedAfterTheIndexWasBuilt_AreFoundForTheirTableOnly()
    {
        int table = NextTableId(), otherTable = table + 1, untouched = table + 2;
        EventSubscriberPatches.ResetForReload();

        // Build the index before the subscribers exist, the way a record construction early in a
        // run does. Nothing targets these ids yet.
        Assert.Empty(EventSubscriberPatches.ValidateSubscriberMethodsForTable(table));
        Assert.Empty(EventSubscriberPatches.TriggerSubscribersForTable(table));

        LoadSubscribers(table, otherTable);

        // Both validate subscribers on `table`, in declaration order; the one on otherTable is not
        // mixed in.
        Assert.Equal(new[] { "QtyValidated", "NoteValidating" },
            Names(EventSubscriberPatches.ValidateSubscriberMethodsForTable(table)));
        Assert.Equal(new[] { "OtherTableQtyValidated" },
            Names(EventSubscriberPatches.ValidateSubscriberMethodsForTable(otherTable)));
        Assert.Empty(EventSubscriberPatches.ValidateSubscriberMethodsForTable(untouched));

        // The table-trigger half: OnAfterInsertEvent is ordinal 2, and it is not reported as a
        // validate subscriber (nor the validate ones as triggers).
        var triggers = EventSubscriberPatches.TriggerSubscribersForTable(table);
        Assert.Equal(new[] { (2, "Inserted") }, triggers.Select(t => (t.EventTypeOrdinal, t.Method.Name)).ToArray());
        Assert.Empty(EventSubscriberPatches.TriggerSubscribersForTable(otherTable));
    }

    [Fact]
    public void AReloadResetRebuildsTheIndexFromTheRescan()
    {
        int table = NextTableId(), otherTable = table + 1;
        EventSubscriberPatches.ResetForReload();
        LoadSubscribers(table, otherTable);
        Assert.Equal(2, EventSubscriberPatches.ValidateSubscriberMethodsForTable(table).Count);

        // A reload clears the registries; the next lookup rescans every loaded assembly and must
        // answer the same, not an empty index left over from the clear and not a doubled one.
        EventSubscriberPatches.ResetForReload();
        Assert.Equal(new[] { "QtyValidated", "NoteValidating" },
            Names(EventSubscriberPatches.ValidateSubscriberMethodsForTable(table)));
        Assert.Single(EventSubscriberPatches.TriggerSubscribersForTable(table));
    }
}
