using System.Reflection;
using System.Reflection.Emit;

namespace AlRunner.Tests;

/// <summary>
/// Finds the places in a method's IL, ahead of a given offset, that leave the method without
/// entering any exception-handling region. Decodes instructions rather than scanning bytes: a
/// raw 0x2A can be an operand byte, and a Debug build emits an early return as a <c>br</c> to
/// one shared <c>ret</c> after the try rather than as a <c>ret</c> (#4822).
/// </summary>
internal static class IlEarlyExits
{
    internal readonly record struct Instruction(int Offset, OpCode Op, int Target);

    private static readonly Dictionary<byte, OpCode> OneByte = new();
    private static readonly Dictionary<byte, OpCode> TwoByte = new();

    static IlEarlyExits()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)f.GetValue(null)!;
            if (op.Size == 1) OneByte[(byte)op.Value] = op;
            else TwoByte[(byte)(op.Value & 0xFF)] = op;
        }
    }

    /// <summary>Decodes every instruction; <c>Target</c> is the branch target, or -1.</summary>
    internal static List<Instruction> Decode(byte[] il)
    {
        var result = new List<Instruction>();
        int i = 0;
        while (i < il.Length)
        {
            int start = i;
            OpCode op;
            if (il[i] == 0xFE)
            {
                if (i + 1 >= il.Length || !TwoByte.TryGetValue(il[i + 1], out op))
                    throw new InvalidOperationException($"undecodable opcode at IL_{start:x4}");
                i += 2;
            }
            else if (!OneByte.TryGetValue(il[i], out op))
                throw new InvalidOperationException($"undecodable opcode at IL_{start:x4}");
            else
                i += 1;

            int target = -1;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget: target = i + 1 + (sbyte)il[i]; i += 1; break;
                case OperandType.InlineBrTarget: target = i + 4 + BitConverter.ToInt32(il, i); i += 4; break;
                case OperandType.ShortInlineI:
                case OperandType.ShortInlineVar: i += 1; break;
                case OperandType.InlineVar: i += 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR: i += 8; break;
                case OperandType.InlineSwitch: i += 4 + 4 * BitConverter.ToInt32(il, i); break;
                default: i += 4; break;
            }
            result.Add(new Instruction(start, op, target));
        }
        if (i != il.Length)
            throw new InvalidOperationException($"decode overran the body: ended at {i}, length {il.Length}");
        return result;
    }

    /// <summary>
    /// Offsets, all before <paramref name="before"/>, of each instruction that exits the method
    /// without entering a protected, filter or handler region: a <c>ret</c> (Release codegen), or an
    /// unconditional <c>br</c> whose straight-line continuation reaches a <c>ret</c> outside every
    /// region (Debug codegen). One site per source-level <c>return</c> in both.
    /// </summary>
    internal static List<int> ExitSitesBefore(MethodBody body, int before)
    {
        var il = body.GetILAsByteArray() ?? throw new InvalidOperationException("no IL body");
        var code = Decode(il);
        var index = code.Select((x, n) => (x.Offset, n)).ToDictionary(p => p.Offset, p => p.n);
        var regions = new List<(int Start, int End)>();
        foreach (var c in body.ExceptionHandlingClauses)
        {
            regions.Add((c.TryOffset, c.TryOffset + c.TryLength));
            regions.Add((c.HandlerOffset, c.HandlerOffset + c.HandlerLength));
            if (c.Flags == ExceptionHandlingClauseOptions.Filter)
                regions.Add((c.FilterOffset, c.HandlerOffset));
        }
        bool Protected(int offset) => regions.Any(r => offset >= r.Start && offset < r.End);

        bool ReachesUnprotectedRet(int offset)
        {
            for (int steps = 0; steps < 32; steps++)
            {
                if (Protected(offset) || !index.TryGetValue(offset, out var n)) return false;
                var x = code[n];
                if (x.Op == OpCodes.Ret) return true;
                if (x.Op == OpCodes.Br || x.Op == OpCodes.Br_S) { offset = x.Target; continue; }
                // A return block only moves the result into place: `ldloc.N; ret` in Debug.
                if (x.Op.FlowControl != FlowControl.Next || x.Op.OperandType != OperandType.InlineNone
                    && x.Op.OperandType != OperandType.ShortInlineVar && x.Op.OperandType != OperandType.InlineVar)
                    return false;
                if (n + 1 >= code.Count) return false;
                offset = code[n + 1].Offset;
            }
            return false;
        }

        return code
            .Where(x => x.Offset < before && !Protected(x.Offset))
            .Where(x => x.Op == OpCodes.Ret
                        || (x.Op == OpCodes.Br || x.Op == OpCodes.Br_S) && ReachesUnprotectedRet(x.Target))
            .Select(x => x.Offset)
            .ToList();
    }
}
