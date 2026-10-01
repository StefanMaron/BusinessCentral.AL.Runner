// LastErrorAccessorsScanTests — #5057: NclCecilRewrite.LastErrorAccessors finds every NavSession
// method that loads or stores the last-error fields, and refuses when that shape no longer holds,
// since a reader it missed would let affectedOnly skip a test whose result changed.
using AlRunner.Infrastructure;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace AlRunner.Tests;

public class LastErrorAccessorsScanTests
{
    // A synthetic NavSession: two last-error fields, a reader, a writer and an unrelated method.
    private static TypeDefinition Session(FieldAttributes access = FieldAttributes.Private, bool withWriter = true,
        bool withReader = true, bool withCallstackField = true)
    {
        var mod = ModuleDefinition.CreateModule("Synthetic5057", ModuleKind.Dll);
        var t = new TypeDefinition("Microsoft.Dynamics.Nav.Runtime", "NavSession", TypeAttributes.Public, mod.TypeSystem.Object);
        mod.Types.Add(t);
        var ex = new FieldDefinition("lastException", access, mod.TypeSystem.Object);
        t.Fields.Add(ex);
        if (withCallstackField) t.Fields.Add(new FieldDefinition("lastErrorCallstack", access, mod.TypeSystem.Object));
        t.Fields.Add(new FieldDefinition("other", FieldAttributes.Private, mod.TypeSystem.Object));

        MethodDefinition Method(string name, params Action<ILProcessor>[] body)
        {
            var m = new MethodDefinition(name, MethodAttributes.Public, mod.TypeSystem.Void);
            var il = m.Body.GetILProcessor();
            foreach (var b in body) b(il);
            il.Emit(OpCodes.Ret);
            t.Methods.Add(m);
            return m;
        }
        if (withReader)
            Method("get_GetLastErrorText", il => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, ex); il.Emit(OpCodes.Pop); });
        if (withWriter)
            Method("ClearLastError", il => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Stfld, ex); });
        Method("Unrelated", il => { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, t.Fields[^1]); il.Emit(OpCodes.Pop); });
        return t;
    }

    [Fact]
    public void FindsTheReaderAndTheWriter_AndNothingElse()
    {
        var found = NclCecilRewrite.LastErrorAccessors(Session());
        Assert.Equal(new[] { ("ClearLastError", true), ("get_GetLastErrorText", false) },
            found.Select(f => (f.Method.Name, f.Writes)).OrderBy(f => f.Name, StringComparer.Ordinal));
    }

    [Fact]
    public void RefusesAFieldThatIsNoLongerPrivate()
        => Assert.Contains("no longer a private instance field",
            Assert.Throws<InvalidOperationException>(() => NclCecilRewrite.LastErrorAccessors(Session(FieldAttributes.Public))).Message);

    [Fact]
    public void RefusesAMissingField()
        => Assert.Contains("lastErrorCallstack",
            Assert.Throws<InvalidOperationException>(() => NclCecilRewrite.LastErrorAccessors(Session(withCallstackField: false))).Message);

    [Fact]
    public void RefusesWhenNoWriterIsFound()
        => Assert.Contains("expected both readers and writers",
            Assert.Throws<InvalidOperationException>(() => NclCecilRewrite.LastErrorAccessors(Session(withWriter: false))).Message);

    [Fact]
    public void RefusesWhenNoReaderIsFound()
        => Assert.Contains("expected both readers and writers",
            Assert.Throws<InvalidOperationException>(() => NclCecilRewrite.LastErrorAccessors(Session(withReader: false))).Message);
}
