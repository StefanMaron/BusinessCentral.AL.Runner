// NavAppExtraProviderLatchTests — the NAV App Extra (2000000157) provider handout: what a
// PARTIAL answer from BC's own provider does, and what two concurrent handouts do (#3315).
//
// WHY THIS IS A RUNNER-SIDE MECHANISM TEST AND NOT AN AL BUNDLE
//   Every claim here is about C# control flow inside
//   AlRunner/Patches/RecordPatches.NavAppExtraVirtualTable.cs: whether a mid-enumeration
//   throw from Ncl's NavAppExtraDataProvider latches the store as answered, whether the
//   ConditionalWeakTable that records the latch survives concurrent handouts, and whether
//   the reflection ready-flag is published before the members it promises. No AL statement
//   can make BC's provider throw after yielding rows — measured, it yields 0 rows in this
//   runner and the loaded-module fallback is what answers — so no bundle under
//   tests/runner-extras/ can drive any of it. Same shape as VirtualTableRefusalClaimTests.
//
// WHAT WAS WRONG (all three on main as of fe8a8f26, none reachable today)
//   1. `if (inserted > 0) latch` cannot tell a COMPLETE BC answer from a partial one. Three
//      rows then a throw latched the store, skipped the fallback, and left the apps BC never
//      reached reading `false` for both Published Application FlowFields — the exact wrong
//      answer #3072 and #3308 exist to remove, on a path that logged nothing.
//   2. ConditionalWeakTable.Add after a TryGetValue throws ArgumentException on a duplicate
//      key, so two concurrent handouts for one store race into an unexplained failure.
//   3. TryEnsureNavAppExtraReflection set _naeReflectionReady = true BEFORE resolving the
//      ctor and the method, so a caller arriving in that window read "ready", found nulls,
//      and silently skipped BC's own provider.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// Joins the serial collection: two tests here mutate RecordPatches statics — the reflection
// ready-flag and its three resolved members, restored in a finally — and a parallel class must
// not observe them nulled.
[Collection(RecordPatchesSerialCollection.Name)]
public sealed class NavAppExtraProviderLatchTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));

    // ── helpers ─────────────────────────────────────────────────────────────────────────

    /// <summary>Yields <paramref name="rows"/> row objects and then throws, the way a lazy
    /// BC provider enumeration fails: after the caller has already taken rows from it.</summary>
    private static IEnumerable YieldThenThrow(int rows, Exception fault)
    {
        for (var i = 0; i < rows; i++) yield return new object();
        throw fault;
    }

    private static IEnumerable Yield(int rows)
    {
        for (var i = 0; i < rows; i++) yield return new object();
    }

    private static object Invoke(string name, params object?[] args)
    {
        var m = typeof(RecordPatches).GetMethod(
            name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(m != null, $"RecordPatches.{name} not found — renamed or removed.");
        try
        {
            return m!.Invoke(null, args)!;
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
    }

    private static (int Inserted, Exception? Fault) Consume(IEnumerable rows, Action<object>? insert = null)
    {
        var outcome = Invoke("ConsumeNavAppExtraProviderRows", rows, insert ?? (_ => { }));
        var t = outcome.GetType();
        return ((int)t.GetProperty("Inserted", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(outcome)!,
                (Exception?)t.GetProperty("Fault", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(outcome));
    }

    private static string Decide(IEnumerable rows)
    {
        var outcome = Invoke("ConsumeNavAppExtraProviderRows", rows, (Action<object>)(_ => { }));
        var decide = typeof(RecordPatches).GetMethod(
            "DecideNavAppExtraBcAnswer", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.True(decide != null, "RecordPatches.DecideNavAppExtraBcAnswer not found.");
        return decide!.Invoke(null, new[] { outcome })!.ToString()!;
    }

    // ── 1. a partial BC answer must not latch ───────────────────────────────────────────

    [Fact]
    public void PartialProviderAnswer_IsRefused_NotLatchedAsComplete()
    {
        var fault = new InvalidOperationException("the metadata retriever went away mid-enumeration");
        var seen = new List<object>();

        var (inserted, captured) = Consume(YieldThenThrow(3, fault), seen.Add);

        // The rows BC did produce were taken, and the exception that ended the enumeration is
        // carried out rather than discarded — all three catch blocks used to swallow it whole.
        Assert.Equal(3, inserted);
        Assert.Equal(3, seen.Count);
        Assert.Same(fault, captured);

        // And the decision: a partial answer is NOT a complete one. Latching it here is the
        // defect — it skips the loaded-module fallback for every app BC never reached.
        Assert.Equal("Refuse", Decide(YieldThenThrow(3, fault)));
    }

    [Fact]
    public void PartialProviderAnswer_UnwrapsAReflectionWrapper_SoTheMessageNamesTheRealFault()
    {
        var real = new NotSupportedException("NavAppGroup is BaseGroup");
        var (inserted, captured) = Consume(YieldThenThrow(1, new TargetInvocationException(real)));

        Assert.Equal(1, inserted);
        Assert.Same(real, captured);
    }

    [Fact]
    public void FaultBeforeAnyRow_FallsBackRatherThanRefusing()
    {
        // The negative arm, and the one that keeps the fix from being "refuse on any throw".
        // Nothing was inserted, so the store is untouched and the loaded-module list can
        // answer the whole table — which is what already happens on every BC build measured.
        var (inserted, captured) = Consume(YieldThenThrow(0, new InvalidOperationException("boom")));

        Assert.Equal(0, inserted);
        Assert.NotNull(captured);
        Assert.Equal("FallBack", Decide(YieldThenThrow(0, new InvalidOperationException("boom"))));
    }

    [Fact]
    public void CompleteNonEmptyAnswer_Latches_AndCompleteEmptyAnswerFallsBack()
    {
        Assert.Equal("Latch", Decide(Yield(4)));
        Assert.Equal("FallBack", Decide(Yield(0)));
    }

    [Fact]
    public void TheRefusalNamesTheProvider_TheFault_AndHowManyRowsArrived()
    {
        var message = (string)Invoke(
            "NavAppExtraPartialAnswerDetail", 3, new InvalidOperationException("retriever vanished"));

        Assert.Contains("NavAppExtraDataProvider.GetAllItems", message, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", message, StringComparison.Ordinal);
        Assert.Contains("retriever vanished", message, StringComparison.Ordinal);
        Assert.Contains("3 row", message, StringComparison.Ordinal);
        Assert.Contains("3315", message, StringComparison.Ordinal);
    }

    // ── 2a. the latch must survive concurrent handouts ──────────────────────────────────

    [Fact]
    public void MarkingTheStoreAnswered_IsSafeUnderConcurrentHandouts()
    {
        var store = new object();

        // ConditionalWeakTable.Add throws ArgumentException on a duplicate key, and the
        // TryGetValue in front of it is not a lock: 64 handouts for one store hit it together.
        var faults = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        Parallel.For(0, 64, _ =>
        {
            try { Invoke("MarkNavAppExtraBcProviderAnswered", store); }
            catch (Exception ex) { faults.Add(ex); }
        });

        Assert.True(faults.IsEmpty,
            "marking the store answered threw under concurrency: "
            + string.Join("; ", faults.Select(e => e.GetType().Name + ": " + e.Message).Distinct()));

        // And it really recorded the latch, so the test cannot pass by doing nothing.
        var field = typeof(RecordPatches).GetField(
            "_naeBcProviderAnswered", BindingFlags.Static | BindingFlags.NonPublic)!;
        var table = (System.Runtime.CompilerServices.ConditionalWeakTable<object, object>)field.GetValue(null)!;
        Assert.True(table.TryGetValue(store, out _));
    }

    // ── 2b. the reflection latch must be published AFTER the members it promises ────────

    [Fact]
    public void ReflectionReadyFlag_IsSetOnlyAfterTheMembersAreResolved()
    {
        var readyField = typeof(RecordPatches).GetField(
            "_naeReflectionReady", BindingFlags.Static | BindingFlags.NonPublic)!;
        var typeField = typeof(RecordPatches).GetField(
            "_naeProviderType", BindingFlags.Static | BindingFlags.NonPublic)!;
        var ctorField = typeof(RecordPatches).GetField(
            "_naeProviderCtor", BindingFlags.Static | BindingFlags.NonPublic)!;
        var methodField = typeof(RecordPatches).GetField(
            "_naeGetAllItems", BindingFlags.Static | BindingFlags.NonPublic)!;

        var savedReady = readyField.GetValue(null);
        var savedType = typeField.GetValue(null);
        var savedCtor = ctorField.GetValue(null);
        var savedMethod = methodField.GetValue(null);
        var probeField = typeof(RecordPatches).GetField(
            "NavAppExtraReflectionProbe", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)!;
        var probe = (System.Threading.AsyncLocal<Action?>)probeField.GetValue(null)!;

        object? readyDuringResolution = null;
        try
        {
            readyField.SetValue(null, false);
            typeField.SetValue(null, null);
            ctorField.SetValue(null, null);
            methodField.SetValue(null, null);
            probe.Value = () => readyDuringResolution = readyField.GetValue(null);

            Invoke("TryEnsureNavAppExtraReflection");
        }
        finally
        {
            probe.Value = null;
            readyField.SetValue(null, savedReady);
            typeField.SetValue(null, savedType);
            ctorField.SetValue(null, savedCtor);
            methodField.SetValue(null, savedMethod);
        }

        Assert.NotNull(readyDuringResolution);
        Assert.False((bool)readyDuringResolution!,
            "_naeReflectionReady was already true while the ctor and method were still being "
            + "resolved — a concurrent caller in that window reads 'ready', finds nulls, and "
            + "silently skips BC's own provider.");
    }

    // ── 2c. a row's identity must be a property of the app, not of insertion order ──────

    [Fact]
    public void RowIdentity_IsTheSameForOneApp_WhateverOrderItWasInsertedIn()
    {
        var app = Guid.Parse("11111111-2222-3333-4444-555555555555");

        var first = (object[])Invoke("NavAppExtraSystemIdArgs", app);
        var second = (object[])Invoke("NavAppExtraSystemIdArgs", app);

        Assert.Equal(first, second);
        Assert.Equal(2000000157, first[0]);
    }

    [Fact]
    public void RowIdentity_DiffersBetweenApps_SoTwoAppsCannotShareASystemId()
    {
        var ids = new[]
        {
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("437dbf0e-84ff-417a-965d-ed2bb9650972"),   // System Application
            Guid.Parse("63ca2fa4-4f03-4f2b-a480-172fef340d3f"),   // Base Application
            Guid.Empty,
        }.Select(g => string.Join(",", ((object[])Invoke("NavAppExtraSystemIdArgs", g)).Select(o => o!.ToString())))
         .ToList();

        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    // ── the same latch shape, everywhere it is written ──────────────────────────────────

    [Fact]
    public void NoLatchTable_StillUsesAdd_WhichThrowsOnADuplicateKey()
    {
        // Question 1 of "fix the shape, not just the reported line": the NAV App Extra latch
        // was not the only one. Every ConditionalWeakTable<object, object> under
        // AlRunner/Patches/ is a "has this store/provider been populated" latch guarded by a
        // TryGetValue that is not a lock, and five of them wrote it with Add — Feature Key,
        // Session, Time Zone, Windows Language and All Profile. AddOrUpdate is the same
        // one-token change in each. Asserted over the field names rather than a fixed file
        // list, so a NEW latch written with Add fails here without anyone remembering to.
        var patches = Directory.GetFiles(
            Path.Combine(RepoRoot, "AlRunner", "Patches"), "*.cs", SearchOption.AllDirectories);

        // The one exclusion, and it is measured rather than defensive:
        // RunObjectMetadataPopulateOnce holds `lock (_omsPopulatedByProvider)` across its
        // TryGetValue and its Add, so no second caller can reach the Add with the key already
        // present. That is a different — and equally correct — answer to the same question.
        var lockGuarded = new[] { "_omsPopulatedByProvider" };

        var offenders = new List<string>();
        foreach (var file in patches)
        {
            var src = File.ReadAllText(file);
            var fields = Regex.Matches(src, @"ConditionalWeakTable<object, object>\s+(_\w+)")
                .Select(m => m.Groups[1].Value).ToList();
            foreach (var f in fields.Where(f => !lockGuarded.Contains(f)))
                foreach (Match hit in Regex.Matches(src, Regex.Escape(f) + @"\.Add\("))
                    offenders.Add($"{Path.GetFileName(file)}: {f}.Add(");
        }

        Assert.True(offenders.Count == 0,
            "ConditionalWeakTable.Add throws ArgumentException when two concurrent handouts "
            + "race past the TryGetValue in front of it; use AddOrUpdate. Offenders: "
            + string.Join(", ", offenders.Distinct()));
    }

    [Fact]
    public void ARefusedStore_ReplaysTheSameRefusalOnEveryLaterHandout()
    {
        // The rows BC inserted before it threw stay in the store and nothing can take them
        // out, so a later handout must not be allowed to fall back and put the runner's own
        // rows alongside them — both insert paths swallow NavRecordAlreadyExistsException, so
        // that mixing would be silent.
        var store = new object();
        var outcomeType = typeof(RecordPatches).GetNestedType(
            "NavAppExtraProviderOutcome", BindingFlags.NonPublic | BindingFlags.Public)!;
        var outcome = Activator.CreateInstance(
            outcomeType,
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object?[] { 3, new InvalidOperationException("retriever vanished"), "enumerating its rows" },
            culture: null)!;

        Assert.Null(Record.Exception(() => Invoke("ThrowIfNavAppExtraBcProviderRefused", store)));

        Invoke("MarkNavAppExtraBcProviderRefused", store, outcome);

        var again = Assert.Throws<AlRunner.Infrastructure.RunnerOutOfScopeException>(
            () => Invoke("ThrowIfNavAppExtraBcProviderRefused", store));
        Assert.Equal("NAV App Extra (virtual table 2000000157)", again.Api);
        Assert.Contains("stopped after 3 row", again.Reason, StringComparison.Ordinal);
        Assert.Contains("retriever vanished", again.Reason, StringComparison.Ordinal);

        // A store marked ANSWERED is not a refused one, so the ordinary latch still lets the
        // handout through rather than throwing at everything.
        var answered = new object();
        Invoke("MarkNavAppExtraBcProviderAnswered", answered);
        Assert.Null(Record.Exception(() => Invoke("ThrowIfNavAppExtraBcProviderRefused", answered)));
    }

    [Fact]
    public void TheFallBackWarning_IsTaggedWarn_AndNamesTheStageAndTheFault()
    {
        var text = (string)Invoke(
            "NavAppExtraProviderFaultWarning", "calling GetAllItems",
            new NotSupportedException("no metadata retriever"));

        // `[warn]`, not a component tag: Log's default filter is what decides whether this is
        // seen at all (#2461, #3068).
        Assert.StartsWith("[warn] ", text, StringComparison.Ordinal);
        Assert.Contains("NavAppExtraDataProvider", text, StringComparison.Ordinal);
        Assert.Contains("calling GetAllItems", text, StringComparison.Ordinal);
        Assert.Contains("NotSupportedException", text, StringComparison.Ordinal);
        Assert.Contains("no metadata retriever", text, StringComparison.Ordinal);
        Assert.Contains("3315", text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFallBackWarning_IsWrittenOncePerProcessPerFault_NotPerHandout()
    {
        // This runs on EVERY data-access handout for 2000000157. A warning repeated per
        // handout is a warning nobody reads.
        var stage = "a stage no other test uses " + Guid.NewGuid();
        var fault = new InvalidTimeZoneException("probe");

        var saved = Console.Error;
        var captured = new System.IO.StringWriter();
        try
        {
            Console.SetError(captured);
            Invoke("WarnOnceNavAppExtraProviderFault", stage, fault);
            Invoke("WarnOnceNavAppExtraProviderFault", stage, fault);
            Invoke("WarnOnceNavAppExtraProviderFault", stage, new InvalidTimeZoneException("again"));
        }
        finally
        {
            Console.SetError(saved);
        }

        var lines = captured.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Contains(stage, StringComparison.Ordinal))
            .ToList();

        Assert.Single(lines);
        Assert.Contains("InvalidTimeZoneException", lines[0], StringComparison.Ordinal);
    }
}
