// TestDataProvisionerTallyAtomicityTests — the mechanism guard for issue #2997.
//
// WHAT IS UNDER TEST, AND WHAT THIS DELIBERATELY DOES NOT CLAIM
//   TestDataProvisioner's --test-data tallies (_tablesDone, _rowsDone, _refused,
//   _readerRefused, _droppedColumns, _columnsNotInThisBuild, _deferredLoadsWrittenOff) were
//   plain `int++` / `+=` written from LoadOnDemand and NoteDeferredLoadWrittenOff, which two
//   threads can be inside at once — TestExecutor.InvokeWithTimeout runs every [Test] on its own
//   worker thread and does NOT kill it when the watchdog expires, so an abandoned thread keeps
//   hydrating while the bundle loop carries on in the same process (the route #2914 established).
//   A read-modify-write from two threads can drop a count.
//
//   These counters are DIAGNOSTICS. Nothing branches on them: they are the numbers the
//   --test-data summary prints, plus DeferredLoadsWrittenOff, which one test asserts. A lost
//   count misreports a run that had already gone abnormal (a test overran its watchdog and the
//   suite was abandoned mid-bundle). It is not data corruption, and #2914 — merged — is what
//   covers the structure that does matter.
//
//   An off-by-one under a genuine race is not deterministically reproducible, so this file does
//   NOT try to reproduce one. It pins the MECHANISM over the compiled IL of the loaded
//   al-runner.dll: every write to a tally goes through System.Threading.Interlocked, and the
//   only plain store left in the type is ResetForTests's (which runs with no other thread in
//   play). That is a deterministic RED → GREEN — it failed on the unfixed code and passes on the
//   fixed code — and it is exactly as strong as its wording: it proves each increment is atomic,
//   not that any particular interleaving was ever observed.
//
//   The behavioural half — eight threads through the real write-off path, asserting the exact
//   total — is TestDataLazyLoadPolicyTests.TheWriteOffTally_IsExactUnderConcurrentWriteOffs.
//   That one is a hammer, not a forced interleaving; its limits are stated there.
//
//   The IL is read rather than the source text because there is no reflection surface for "how
//   was this field written", and a source-text scan of this repo has failed under Linux CI
//   before (see InstallBaselineAppendConcurrencyTests, which pins #2914's walkers the same way).
using AlRunner;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestDataProvisionerTallyAtomicityTests
{
    /// <summary>Every running tally on TestDataProvisioner. Named individually rather than
    /// discovered, so adding a counter without deciding how it is written fails review here
    /// rather than shipping unsynchronised.</summary>
    public static TheoryData<string> Tallies => new()
    {
        "_tablesDone", "_rowsDone", "_refused", "_readerRefused",
        "_droppedColumns", "_columnsNotInThisBuild", "_deferredLoadsWrittenOff",
    };

    private static TypeDefinition Provisioner()
    {
        var asm = AssemblyDefinition.ReadAssembly(typeof(TestDataProvisioner).Assembly.Location);
        var type = asm.MainModule.GetType(typeof(TestDataProvisioner).FullName);
        Assert.True(type != null, "TestDataProvisioner not found in the loaded al-runner.dll");
        return type!;
    }

    /// <summary>The type plus anything the compiler generated inside it (closures, iterator
    /// state machines): a lambda writing a tally is still a write to a tally.</summary>
    private static IEnumerable<MethodDefinition> AllMethods(TypeDefinition type)
        => type.Methods.Concat(type.NestedTypes.SelectMany(n => n.Methods)).Where(m => m.HasBody);

    private static bool Touches(Instruction i, OpCode op, string field)
        => i.OpCode == op && i.Operand is FieldReference fr && fr.Name == field;

    /// <summary>
    /// The claim, first half: nothing writes a tally with a plain store except ResetForTests.
    /// `x++` and `x += n` on a static field compile to ldsfld/add/stsfld — a read-modify-write
    /// two threads can interleave — so the ABSENCE of stsfld outside the reset is the absence of
    /// the defect. Against the unfixed code LoadOnDemand carries five of these and
    /// NoteDeferredLoadWrittenOff the sixth.
    ///
    /// ResetForTests is the one legitimate plain writer and is asserted as such below, so this
    /// cannot be satisfied by deleting the reset.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tallies))]
    public void NoMethodButResetForTests_StoresATallyDirectly(string field)
    {
        var offenders = AllMethods(Provisioner())
            .Where(m => m.Name != nameof(TestDataProvisioner.ResetForTests))
            .Where(m => m.Body.Instructions.Any(i => Touches(i, OpCodes.Stsfld, field)))
            .Select(m => m.Name)
            .ToArray();

        Assert.True(offenders.Length == 0,
            $"{field} is written with a plain stsfld by: {string.Join(", ", offenders)}. "
            + "That is a read-modify-write two --test-data hydration threads can interleave "
            + "(#2997); use Interlocked.Increment/Add.");
    }

    /// <summary>
    /// Second half, and the one that makes the first non-vacuous: the tally really is still
    /// counted, and counted atomically. A "fix" that simply deleted every increment would pass
    /// the stsfld check above and fail here.
    ///
    /// Matched as "the address of the field is taken, and an Interlocked call consumes it" —
    /// Interlocked.Increment/Add take `ref int`, which is ldsflda, and no other shape in this
    /// type does.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tallies))]
    public void EveryTally_IsMutatedThroughInterlocked(string field)
    {
        var sites = 0;
        foreach (var m in AllMethods(Provisioner()))
        {
            foreach (var i in m.Body.Instructions.Where(x => Touches(x, OpCodes.Ldsflda, field)))
            {
                // Interlocked.Increment(ref f) calls immediately; Interlocked.Add(ref f, n)
                // pushes its addend first, and the addend can itself be a call (a property
                // getter on the hydration result), so scan a short window rather than requiring
                // the next instruction. Another field address appearing first ends the window —
                // that would be a different call's argument, not this one's.
                for (var next = i.Next; next != null; next = next.Next)
                {
                    if (next.OpCode == OpCodes.Ldsflda || next.OpCode == OpCodes.Ldflda) break;
                    if ((next.OpCode == OpCodes.Call || next.OpCode == OpCodes.Callvirt)
                        && next.Operand is MethodReference mr
                        && mr.DeclaringType.FullName == "System.Threading.Interlocked")
                    {
                        sites++;
                        break;
                    }
                    if (next.Offset - i.Offset > 40) break;   // bounded: this is one call, not a block
                }
            }
        }

        Assert.True(sites > 0,
            $"{field} is never passed to System.Threading.Interlocked, so either it is no "
            + "longer counted at all or it is counted by some other unsynchronised means (#2997).");
    }

    /// <summary>
    /// ResetForTests stays a plain store, deliberately: it runs between runs with no other
    /// thread in play, and routing it through Interlocked would suggest a concurrency it does
    /// not have. Asserted so the exemption in the first test is a stated decision rather than a
    /// hole — if the reset ever stops writing these fields, the first test goes vacuous and this
    /// one says so.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tallies))]
    public void ResetForTests_StillClearsEveryTally(string field)
    {
        var reset = Provisioner().Methods.Single(m => m.Name == nameof(TestDataProvisioner.ResetForTests));
        Assert.True(reset.Body.Instructions.Any(i => Touches(i, OpCodes.Stsfld, field)),
            $"ResetForTests no longer clears {field}, so a stale count survives into the next run.");
    }
}
