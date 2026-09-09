// TestPageWriteBufferTests — the C# unwind policy behind the TestPage write buffer (#3640).
//
// What this pins is the RUNNER's own mechanism, not what BC does. The BC claim — that a
// page-driven write which raises leaves none of its triggers' Rec mutations visible — is
// stated upstream by StefanMaron/BusinessCentral.AL.Language.Tests#309, which is where a
// real service tier adjudicates it. Duplicating that claim here would only prove the runner
// agrees with itself.
//
// What IS provable without a loaded BC runtime is the policy: a refused write is unwound to
// the values the buffer held before it, a successful one is left alone, the original
// exception reaches the caller unchanged, and one field that refuses restoration does not
// abandon the rest of the row half-restored. FakeBuffer stands in for the NavRecord-backed
// buffer; the production implementation differs only in where the field numbers and values
// come from.
using System;
using System.Collections.Generic;
using System.Linq;
using AlRunner.Patches;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public sealed class TestPageWriteBufferTests
{
    private sealed class FakeBuffer : TestPageWriteBuffer.IRestorableBuffer
    {
        private readonly Dictionary<int, object?> _values;

        // Field numbers this buffer refuses to have written back, so the per-field
        // fault-tolerance arm has something concrete to fail on.
        private readonly HashSet<int> _unwritable;

        // Set when RestorableFieldNos is enumerated with this true, so the snapshot-failure
        // arm can make the READ side raise rather than the write side.
        internal bool FailOnRead { get; set; }

        internal int WriteCount { get; private set; }

        internal FakeBuffer(Dictionary<int, object?> values, params int[] unwritable)
        {
            _values = values;
            _unwritable = new HashSet<int>(unwritable);
        }

        public IEnumerable<int> RestorableFieldNos => _values.Keys.ToArray();

        public object? Read(int fieldNo)
        {
            if (FailOnRead) throw new InvalidOperationException("record is stale or not open");
            return _values[fieldNo];
        }

        public void Write(int fieldNo, object? value)
        {
            WriteCount++;
            if (_unwritable.Contains(fieldNo))
                throw new InvalidOperationException($"field {fieldNo} refuses a write");
            _values[fieldNo] = value;
        }

        internal object? Peek(int fieldNo) => _values[fieldNo];
    }

    private sealed class RefusalException : Exception
    {
        internal RefusalException(string message) : base(message) { }
    }

    [Fact]
    public void ARefusedWrite_UnwindsEveryFieldItMutated()
    {
        var buffer = new FakeBuffer(new Dictionary<int, object?> { [1] = 3, [2] = "", [5] = "" });

        Assert.Throws<RefusalException>(() =>
            TestPageWriteBuffer.RunRestoringOnRefusal(buffer, () =>
            {
                // What a before-validate trigger would do, then the refusal it raises.
                buffer.Write(5, "before;");
                buffer.Write(2, "stop");
                throw new RefusalException("MCV stopped in OnBeforeValidate");
            }));

        Assert.Equal(3, buffer.Peek(1));
        Assert.Equal("", buffer.Peek(2));
        Assert.Equal("", buffer.Peek(5));
    }

    [Fact]
    public void ARefusedWrite_RethrowsTheOriginalExceptionUnchanged()
    {
        var buffer = new FakeBuffer(new Dictionary<int, object?> { [5] = "" });

        var thrown = Assert.Throws<RefusalException>(() =>
            TestPageWriteBuffer.RunRestoringOnRefusal(buffer, () =>
            {
                buffer.Write(5, "before;");
                throw new RefusalException("MCV stopped in OnBeforeValidate");
            }));

        // The type AND the message, because BC's NavTestField.CheckError records the message
        // and AL's asserterror matches on it — swallowing or wrapping the refusal here would
        // turn a refused write into a silent one.
        Assert.Equal("MCV stopped in OnBeforeValidate", thrown.Message);
    }

    [Fact]
    public void ASuccessfulWrite_IsLeftExactlyAsItWasWritten()
    {
        var buffer = new FakeBuffer(new Dictionary<int, object?> { [2] = "", [5] = "" });

        TestPageWriteBuffer.RunRestoringOnRefusal(buffer, () =>
        {
            buffer.Write(2, "value");
            buffer.Write(5, "before;page;after;");
        });

        Assert.Equal("value", buffer.Peek(2));
        Assert.Equal("before;page;after;", buffer.Peek(5));
    }

    [Fact]
    public void ASuccessfulWrite_WritesNothingOfItsOwn()
    {
        var buffer = new FakeBuffer(new Dictionary<int, object?> { [2] = "", [5] = "" });

        TestPageWriteBuffer.RunRestoringOnRefusal(buffer, () => buffer.Write(2, "value"));

        // Exactly the one write the body made. A restore on the success path would be
        // invisible in the VALUES (they would be re-written to what they already are) and is
        // only observable as extra writes — which on a real NavRecord means extra
        // SetFieldValue calls through BC's own validate-free store path.
        Assert.Equal(1, buffer.WriteCount);
    }

    [Fact]
    public void OneFieldRefusingRestoration_DoesNotAbandonTheRest()
    {
        // Field 2 refuses a write back; 1 and 5 must still be restored.
        var buffer = new FakeBuffer(new Dictionary<int, object?> { [1] = 3, [2] = "", [5] = "" }, unwritable: 2);

        Assert.Throws<RefusalException>(() =>
            TestPageWriteBuffer.RunRestoringOnRefusal(buffer, () =>
            {
                buffer.Write(1, 99);
                buffer.Write(5, "before;");
                throw new RefusalException("refused");
            }));

        Assert.Equal(3, buffer.Peek(1));
        Assert.Equal("", buffer.Peek(5));
    }

    [Fact]
    public void ABufferThatCannotBeRead_LeavesTheWriteUnwrapped()
    {
        var buffer = new FakeBuffer(new Dictionary<int, object?> { [5] = "" }) { FailOnRead = true };

        // The snapshot fails, so nothing can be restored — but the write must still run and
        // its refusal must still reach the caller. Refusing the write outright would be a
        // worse error than not unwinding it: BC allows the write.
        Assert.Throws<RefusalException>(() =>
            TestPageWriteBuffer.RunRestoringOnRefusal(buffer, () =>
            {
                buffer.FailOnRead = false;
                buffer.Write(5, "before;");
                throw new RefusalException("refused");
            }));

        Assert.Equal("before;", buffer.Peek(5));
    }

    [Fact]
    public void ANullBuffer_RunsTheWriteRatherThanRefusingIt()
    {
        // The record-less binding: a page over no source table has no Rec to unwind.
        var ran = false;
        TestPageWriteBuffer.RunRestoringOnRefusal((TestPageWriteBuffer.IRestorableBuffer?)null,
            () => ran = true);
        Assert.True(ran);
    }

    // The two callers, pinned in IL — a wiring claim, so that removing the unwind from either
    // write path fails here rather than only in the corpus.
    //
    // The Rec-bound path is the one with a constraint on HOW it calls: it snapshots inline and
    // restores in its own catch, because TestPageNewRowLinePromotionTests reads
    // LiveNavTestField.Write's IL for the #2923 ordering and a lambda would hide all three of
    // its markers inside a closure. This arm holds that spelling in place; without it a later
    // editor tidying Write into RunRestoringOnRefusal turns the ordering test green-for-the-
    // wrong-reason and nothing objects.
    [Theory]
    [InlineData("AlRunner.LiveNavTestField", "Write", "Snapshot")]
    [InlineData("AlRunner.PageVariableTestField", "set_Value", "RunRestoringOnRefusal")]
    public void BothWritePathsUnwindTheirBuffer(string typeName, string methodName, string expectedCall)
    {
        var module = AssemblyDefinition
            .ReadAssembly(typeof(TestPageWriteBuffer).Assembly.Location).MainModule;
        var type = module.GetType(typeName);
        Assert.NotNull(type);

        // The call can sit in the method itself or in a closure the compiler lifted out of it,
        // which is exactly the difference the two spellings are about — so both are searched
        // and the ASSERTION is on which entry point is reached, not on where it is reached from.
        var bodies = type!.Methods.Where(m => m.HasBody && m.Name == methodName)
            .Concat(type.NestedTypes.SelectMany(n => n.Methods).Where(m => m.HasBody));

        var calls = bodies
            .SelectMany(m => m.Body.Instructions)
            .Where(i => i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt)
            .Select(i => i.Operand as MethodReference)
            .Where(mr => mr != null && mr.DeclaringType.Name == nameof(TestPageWriteBuffer))
            .Select(mr => mr!.Name)
            .ToList();

        Assert.Contains(expectedCall, calls);
    }
}
