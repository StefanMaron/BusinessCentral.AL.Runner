// RecordPatches.CodeunitParameterTransfer — how a method's async state machine copies each
// parameter into its ALMethodScope, read from the IL of MoveNext (#4601). Two facts the C#
// signature does not carry are stated there: a by-value Text/Code parameter is copied through
// ModifyLength(N) — N is the declared length, 0 for an unbounded one — and a by-value Interface
// parameter through ALByValue, while a `var` one is stored as-is. Harness-only, like the rest of
// the method table. See docs/codeunit-metadata-from-bc.md#what-the-method-body-states.

using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    /// <summary>How one parameter reaches the method scope.</summary>
    internal enum ParameterTransferKind { Direct, ByValue, ModifyLength }

    internal readonly record struct ParameterTransfer(ParameterTransferKind Kind, int Length);

    private static readonly Dictionary<short, OpCode> IlOpCodes = typeof(OpCodes)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Select(f => (OpCode)f.GetValue(null)!)
        .ToDictionary(o => o.Value);

    /// <summary>
    /// Per parameter name, the FIRST thing MoveNext does with the state-machine field holding it:
    /// <c>ldfld p; ldc.i4 N; call ModifyLength</c>, <c>ldfld p; …; call ALByValue</c>, or
    /// <c>ldfld p; stfld</c>. Null — never a partial answer — when the method has no async state
    /// machine or its IL cannot be read, so the caller refuses.
    /// </summary>
    private static Dictionary<string, ParameterTransfer>? ReadParameterTransfers(
        MetadataReader md, PEReader pe, TypeDefinition owner, MethodDefinition method, out string why)
    {
        var machine = FindAsyncStateMachine(md, owner, method);
        if (machine is null) { why = "the method has no async state machine to read its parameter copies from"; return null; }
        var moveNext = machine.Value.GetMethods().Select(md.GetMethodDefinition)
            .Where(m => md.GetString(m.Name) == "MoveNext").ToList();
        if (moveNext is not [var body] || body.RelativeVirtualAddress == 0)
        {
            why = "the async state machine has no readable MoveNext";
            return null;
        }

        byte[] il;
        try { il = pe.GetMethodBody(body.RelativeVirtualAddress).GetILBytes() ?? []; }
        catch (BadImageFormatException ex) { why = $"MoveNext IL unreadable: {ex.Message}"; return null; }

        var instructions = DecodeIl(il);
        if (instructions is null) { why = "MoveNext IL uses an opcode the reader does not know"; return null; }

        var result = new Dictionary<string, ParameterTransfer>(StringComparer.Ordinal);
        for (var i = 0; i < instructions.Count; i++)
        {
            var (op, token) = instructions[i];
            if (op != OpCodes.Ldfld || !TryFieldName(md, token, out var field) || result.ContainsKey(field)) continue;
            if (i + 1 >= instructions.Count) break;

            // ldc.i4 N; call ModifyLength(int)
            if (TryLoadInt(instructions[i + 1], out var length) && i + 2 < instructions.Count
                && IsCallTo(md, instructions[i + 2], "ModifyLength"))
            {
                result[field] = new(ParameterTransferKind.ModifyLength, length);
                continue;
            }
            if (instructions[i + 1].Op == OpCodes.Stfld)
            {
                result[field] = new(ParameterTransferKind.Direct, 0);
                continue;
            }
            // The receiver is loaded, then the tree-object argument, then the call; take the first
            // call after the load and name what it is rather than guessing a transfer.
            for (var j = i + 1; j < instructions.Count && j <= i + 6; j++)
            {
                if (instructions[j].Op != OpCodes.Call && instructions[j].Op != OpCodes.Callvirt) continue;
                if (IsCallTo(md, instructions[j], "ALByValue"))
                    result[field] = new(ParameterTransferKind.ByValue, 0);
                break;
            }
        }
        why = "";
        return result;
    }

    private static TypeDefinition? FindAsyncStateMachine(MetadataReader md, TypeDefinition owner, MethodDefinition method)
    {
        string? machineName = null;
        foreach (var h in method.GetCustomAttributes())
        {
            var ca = md.GetCustomAttribute(h);
            if (AttributeTypeName(md, ca) != "AsyncStateMachineAttribute") continue;
            // One System.Type argument, serialized as a type name after the 0x0001 prolog. Read
            // directly: AttributeArgumentTypes reads every type reference as an enum.
            var blob = md.GetBlobReader(ca.Value);
            if (blob.Length < 3 || blob.ReadUInt16() != 1) return null;
            if (blob.ReadSerializedString() is { } serialized)
                machineName = serialized[(serialized.LastIndexOf('+') + 1)..];
        }
        if (machineName is null) return null;
        foreach (var nestedHandle in owner.GetNestedTypes())
        {
            var nested = md.GetTypeDefinition(nestedHandle);
            if (md.GetString(nested.Name) == machineName) return nested;
        }
        return null;
    }

    private static List<(OpCode Op, int Operand)>? DecodeIl(byte[] il)
    {
        var result = new List<(OpCode, int)>();
        var pos = 0;
        while (pos < il.Length)
        {
            short value = il[pos++];
            if (value == 0xFE)
            {
                if (pos >= il.Length) return null;
                value = unchecked((short)(0xFE00 | il[pos++]));
            }
            if (!IlOpCodes.TryGetValue(value, out var op)) return null;
            var operand = 0;
            switch (op.OperandType)
            {
                case OperandType.InlineNone: break;
                case OperandType.ShortInlineBrTarget:
                case OperandType.ShortInlineVar:
                    operand = il[pos]; pos += 1; break;
                case OperandType.ShortInlineI:
                    operand = unchecked((sbyte)il[pos]); pos += 1; break;
                case OperandType.InlineVar:
                    operand = BitConverter.ToUInt16(il, pos); pos += 2; break;
                case OperandType.InlineI8:
                case OperandType.InlineR:
                    pos += 8; break;
                case OperandType.InlineSwitch:
                    var count = BitConverter.ToInt32(il, pos);
                    pos += 4 + 4 * count; break;
                default: // every remaining operand type is four bytes: tokens, I, BrTarget, ShortInlineR
                    operand = BitConverter.ToInt32(il, pos); pos += 4; break;
            }
            result.Add((op, operand));
        }
        return result;
    }

    private static bool TryLoadInt((OpCode Op, int Operand) instruction, out int value)
    {
        var op = instruction.Op;
        if (op == OpCodes.Ldc_I4 || op == OpCodes.Ldc_I4_S) { value = instruction.Operand; return true; }
        if (op.Value >= OpCodes.Ldc_I4_0.Value && op.Value <= OpCodes.Ldc_I4_8.Value)
        {
            value = op.Value - OpCodes.Ldc_I4_0.Value;
            return true;
        }
        value = 0;
        return false;
    }

    private static bool TryFieldName(MetadataReader md, int token, out string name)
    {
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.FieldDefinition)
        {
            name = md.GetString(md.GetFieldDefinition((FieldDefinitionHandle)handle).Name);
            return true;
        }
        name = "";
        return false;
    }

    private static bool IsCallTo(MetadataReader md, (OpCode Op, int Operand) instruction, string methodName)
    {
        if (instruction.Op != OpCodes.Call && instruction.Op != OpCodes.Callvirt) return false;
        var handle = MetadataTokens.EntityHandle(instruction.Operand);
        return handle.Kind switch
        {
            HandleKind.MemberReference => md.GetString(md.GetMemberReference((MemberReferenceHandle)handle).Name) == methodName,
            HandleKind.MethodDefinition => md.GetString(md.GetMethodDefinition((MethodDefinitionHandle)handle).Name) == methodName,
            _ => false,
        };
    }
}
