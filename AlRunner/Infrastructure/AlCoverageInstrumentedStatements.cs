// AlCoverageInstrumentedStatements — determines which SourceSpans indices a scope class
// actually backs with a runtime hit call (NavMethodScope.StmtHit or .CStmtHit), as
// opposed to indices SourceSpans carries for error-mapping only.
//
// Empirically confirmed (DUMP_CS=1 against two fixtures — AlRunner.Tests/Fixtures/
// RecordTriggerXRec and a scratch if/else probe): BC's compiler always emits one extra
// SourceSpans entry beyond the highest index any StmtHit/CStmtHit call uses (a trailing
// sentinel — observed at the method's closing `end;`). That entry can never register a
// hit no matter what the test does, so including it in a coverage report would
// permanently show one line at 0% regardless of execution — not a real "did not run"
// signal. Filtering to indices this scan actually finds keeps the report honest.
//
// This also finds indices only reachable via CStmtHit, which is the instrumentation an
// `if`/`while`/`repeat` CONDITION gets (its call is folded into the boolean expression:
// `if (CStmtHit(1) & (this.flag))`) as opposed to the StmtHit a plain statement gets.
// Without this, every conditional expression's own line would read permanently 0.
using System.Reflection;
using System.Reflection.Emit;

namespace AlRunner.Infrastructure;

public static class AlCoverageInstrumentedStatements
{
    private static readonly Dictionary<byte, OpCode> SingleByteOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .Where(op => op.Size == 1)
        .ToDictionary(op => unchecked((byte)op.Value));

    private static readonly Dictionary<byte, OpCode> DoubleByteOpCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode))
        .Select(f => (OpCode)f.GetValue(null)!)
        .Where(op => op.Size == 2 && ((ushort)op.Value >> 8) == 0xFE)
        .ToDictionary(op => unchecked((byte)op.Value));

    /// <summary>
    /// Scans <paramref name="scopeType"/>'s OnRun/OnRunAsync/OnRunEventAsync method body
    /// (whichever is present — BC emits exactly one per scope class) for
    /// StmtHit(int)/CStmtHit(int[, bool]) call sites and returns the set of statement
    /// indices actually instrumented.
    /// </summary>
    public static HashSet<int> Find(Type scopeType)
    {
        var result = new HashSet<int>();
        foreach (var m in scopeType.GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
        {
            if (m.Name is not ("OnRun" or "OnRunAsync" or "OnRunEventAsync")) continue;
            byte[]? il;
            try { il = m.GetMethodBody()?.GetILAsByteArray(); }
            catch (Exception) { continue; } // e.g. a method with no body to reflect over
            if (il == null) continue;
            Scan(m.Module, il, result);
        }
        return result;
    }

    private static void Scan(Module module, byte[] il, HashSet<int> result)
    {
        long? lastConst = null;
        int offset = 0;
        while (offset < il.Length)
        {
            byte first = il[offset++];
            OpCode op;
            if (first == 0xFE)
            {
                if (offset >= il.Length || !DoubleByteOpCodes.TryGetValue(il[offset++], out op)) return;
            }
            else if (!SingleByteOpCodes.TryGetValue(first, out op))
            {
                return; // unrecognised opcode — stop scanning defensively; coverage is
                        // best-effort diagnostics, never allowed to crash a test run.
            }

            long? thisConst = null;
            switch (op.OperandType)
            {
                case OperandType.InlineNone:
                    thisConst = ConstFromInlineNone(op);
                    break;
                case OperandType.ShortInlineI:
                    if (offset >= il.Length) return;
                    if (op == OpCodes.Ldc_I4_S) thisConst = unchecked((sbyte)il[offset]);
                    offset += 1;
                    break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineVar:
                    offset += 1;
                    break;
                case OperandType.InlineVar:
                    offset += 2;
                    break;
                case OperandType.InlineI:
                    if (offset + 4 > il.Length) return;
                    if (op == OpCodes.Ldc_I4) thisConst = BitConverter.ToInt32(il, offset);
                    offset += 4;
                    break;
                case OperandType.InlineBrTarget:
                case OperandType.ShortInlineR: // 4 bytes despite the name (float32; InlineR is the 8-byte double)
                    offset += 4;
                    break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    offset += 8;
                    break;
                case OperandType.InlineSwitch:
                    if (offset + 4 > il.Length) return;
                    int caseCount = BitConverter.ToInt32(il, offset);
                    offset += 4 + caseCount * 4;
                    break;
                case OperandType.InlineMethod:
                {
                    if (offset + 4 > il.Length) return;
                    int token = BitConverter.ToInt32(il, offset);
                    offset += 4;
                    if ((op == OpCodes.Call || op == OpCodes.Callvirt) && lastConst.HasValue)
                    {
                        MethodBase? resolved;
                        try { resolved = module.ResolveMethod(token); }
                        catch (Exception) { resolved = null; }
                        if (resolved != null && IsStmtHitFamily(resolved))
                            result.Add(checked((int)lastConst.Value));
                    }
                    break;
                }
                case OperandType.InlineField:
                case OperandType.InlineSig:
                case OperandType.InlineString:
                case OperandType.InlineTok:
                case OperandType.InlineType:
                    offset += 4;
                    break;
                default:
                    return;
            }

            lastConst = thisConst;
        }
    }

    private static long? ConstFromInlineNone(OpCode op)
    {
        if (op == OpCodes.Ldc_I4_M1) return -1;
        if (op == OpCodes.Ldc_I4_0) return 0;
        if (op == OpCodes.Ldc_I4_1) return 1;
        if (op == OpCodes.Ldc_I4_2) return 2;
        if (op == OpCodes.Ldc_I4_3) return 3;
        if (op == OpCodes.Ldc_I4_4) return 4;
        if (op == OpCodes.Ldc_I4_5) return 5;
        if (op == OpCodes.Ldc_I4_6) return 6;
        if (op == OpCodes.Ldc_I4_7) return 7;
        if (op == OpCodes.Ldc_I4_8) return 8;
        return null;
    }

    private static bool IsStmtHitFamily(MethodBase m) =>
        m.DeclaringType?.Namespace == "Microsoft.Dynamics.Nav.Runtime"
        && (m.Name == "StmtHit" || m.Name == "CStmtHit");
}
