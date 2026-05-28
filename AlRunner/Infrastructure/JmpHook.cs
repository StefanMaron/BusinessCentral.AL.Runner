// JmpHook — patches a JIT'd method's entry point with an x86-64 absolute-indirect JMP
// to a replacement method. Uses mprotect to make the page writable.
//
// .NET 8 lays out method entries as one of three precode shapes; we follow them through
// to the actual JIT'd code so the JMP lands in the right place when the original was
// already JIT-compiled before we hooked it.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AlRunnerV2.Infrastructure;

internal static class JmpHook
{
    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(IntPtr addr, nuint len, int prot);
    private const int PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4;

    // Counters + last-attempted bookkeeping for post-mortem diagnosis.
    // If the process SIGSEGVs during patch install, LastAttempt names the hook
    // whose Apply() was in flight (or had just finished) when the crash occurred.
    // `AL_RUNNER_HOOK_TRACE=1` enables a stderr line for every Apply, flushed
    // immediately so the trail survives a crash.
    public static int AppliedCount;
    public static string? LastAttempt;
    private static readonly bool _trace =
        Environment.GetEnvironmentVariable("AL_RUNNER_HOOK_TRACE") == "1";
    private static readonly string _traceLog = "/tmp/al-runner-hook-trace.log";

    private static void TraceLine(string line)
    {
        // File-based trace: console streams get redirected by NavEnvironment.cctor /
        // other early patches, so Console.Error.WriteLine inside the patch-install
        // window vanishes. A direct file write always survives, even across a SIGSEGV
        // because we open-append-close per line.
        try { System.IO.File.AppendAllText(_traceLog, line + "\n"); } catch { }
        try { Console.Error.WriteLine(line); Console.Error.Flush(); } catch { }
    }

    // DIAGNOSTIC: AL_RUNNER_NO_JMPHOOK=1 turns every JmpHook into a no-op so we can
    // A/B whether a hang/regression comes from the JmpHook layer (runtime-native
    // entry-point overwrite, runtime-version-sensitive) vs the Cecil IL-rewrite layer.
    private static readonly bool _disabled =
        Environment.GetEnvironmentVariable("AL_RUNNER_NO_JMPHOOK") == "1";

    public static void Apply(MethodBase original, MethodInfo replacement, string name)
    {
        LastAttempt = name;
        if (_disabled) { if (_trace) TraceLine($"[JmpHook] SKIP (disabled) {name}"); return; }
        if (_trace) TraceLine($"[JmpHook] APPLY BEGIN {name}");
        RuntimeHelpers.PrepareMethod(original.MethodHandle);
        RuntimeHelpers.PrepareMethod(replacement.MethodHandle);
        var origFp = original.MethodHandle.GetFunctionPointer();
        var replFp = replacement.MethodHandle.GetFunctionPointer();

        IntPtr compiledCode = IntPtr.Zero;
        try
        {
            byte[] precode = new byte[24];
            Marshal.Copy(origFp, precode, 0, 24);
            // .NET 8 x64 FixupPrecode: MOV r10,MD ; JMP [rip+disp32]
            if (precode[10] == 0xFF && precode[11] == 0x25)
                compiledCode = Marshal.ReadIntPtr(origFp + 16 + BitConverter.ToInt32(precode, 12));
            // StubPrecode
            if (compiledCode == IntPtr.Zero && precode[0] == 0xFF && precode[1] == 0x25)
                compiledCode = Marshal.ReadIntPtr(origFp + 6 + BitConverter.ToInt32(precode, 2));
            // E9 relative
            if (compiledCode == IntPtr.Zero && precode[0] == 0xE9)
                compiledCode = origFp + 5 + BitConverter.ToInt32(precode, 1);
        }
        catch { }

        WriteJmp(origFp, replFp);
        if (compiledCode != IntPtr.Zero && compiledCode != origFp && compiledCode != replFp)
            try {
                byte[] ccBytes = new byte[8];
                Marshal.Copy(compiledCode, ccBytes, 0, 8);
                // Follow one more level of FF 25 indirection (JMP stub chain)
                if (ccBytes[0] == 0xFF && ccBytes[1] == 0x25)
                {
                    var actualNativeCode = Marshal.ReadIntPtr(compiledCode + 6 + BitConverter.ToInt32(ccBytes, 2));
                    if (actualNativeCode != IntPtr.Zero && actualNativeCode != origFp && actualNativeCode != replFp && actualNativeCode != compiledCode)
                        try { WriteJmp(actualNativeCode, replFp); } catch { }
                }
                WriteJmp(compiledCode, replFp);
            } catch { }
        AppliedCount++;
        if (_trace) TraceLine($"[JmpHook] APPLY END   {name} (#{AppliedCount})");
    }

    private static void WriteJmp(IntPtr target, IntPtr destination)
    {
        // x86-64 absolute indirect: FF 25 00 00 00 00 [imm64]
        byte[] jmp = new byte[14];
        jmp[0] = 0xFF; jmp[1] = 0x25;
        BitConverter.GetBytes(destination.ToInt64()).CopyTo(jmp, 6);
        long pageSize = 4096;
        long addr = target.ToInt64();
        long pageStart = addr & ~(pageSize - 1);
        var regionSize = (nuint)(((addr - pageStart) + jmp.Length + pageSize - 1) & ~(pageSize - 1));
        if (mprotect(new IntPtr(pageStart), regionSize, PROT_READ | PROT_WRITE | PROT_EXEC) != 0)
        {
            Console.Error.WriteLine($"[JmpHook.WriteJmp] mprotect FAILED for target=0x{target:X} errno={Marshal.GetLastSystemError()}");
            return;
        }
        Marshal.Copy(jmp, 0, target, jmp.Length);
    }

    // ── Cell-patch approach: patch the indirection cell, NOT the code bytes ────────────────
    //
    // .NET 8 FixupPrecode layout (x64):
    //   00: FF 25 [disp32]    ; JMP QWORD PTR [rip+disp32]   ← indirect through a memory cell
    //   06: 4C 8B 15 [disp32] ; MOV R10, [rip+disp32]        ← MethodDesc lookup (do NOT corrupt)
    //   0D: FF 25 [disp32]    ; JMP QWORD PTR [rip+disp32]
    //   13: 90...             ; padding
    //
    // The prior spike failed because WriteJmp wrote 14 bytes starting at byte 0, corrupting
    // bytes 6-12 (the MOV R10 / MethodDesc instruction). The JIT reads those bytes when
    // lazily compiling callers → SIGSEGV.
    //
    // InstallIndirect instead:
    //   1. Verifies the FF 25 signature (bytes 0-1).
    //   2. Reads the int32 displacement at offset 2.
    //   3. Computes cell_addr = precode_addr + 6 + disp32 (RIP after the 6-byte JMP instruction).
    //   4. Saves the original pointer from the cell.
    //   5. mprotect the data page (PROT_READ | PROT_WRITE — no EXEC needed for a data page).
    //   6. Atomically writes the replacement function pointer into the cell.
    //   7. Restores page protection to PROT_READ.
    //
    // The MOV R10 bytes (MethodDesc pointer) are never touched → lazy-JIT'd callers safe.
    // Works for async entry points because they are ordinary non-generic instance methods
    // with a standard precode — only their body sets up a state machine.
    //
    /// <summary>
    /// Patches the indirection cell pointed to by the method's precode FF 25 JMP, leaving the
    /// MethodDesc MOV R10 bytes intact. Safe for async entry points and any method whose first
    /// 2 precode bytes are FF 25 (FixupPrecode / StubPrecode indirect JMP).
    /// </summary>
    /// <returns>
    /// True if the cell was patched. False if the precode signature check failed (method uses a
    /// different dispatch shape) — caller should log and skip.
    /// </returns>
    public static bool InstallIndirect(MethodBase original, MethodInfo replacement, string label)
    {
        RuntimeHelpers.PrepareMethod(original.MethodHandle);
        RuntimeHelpers.PrepareMethod(replacement.MethodHandle);

        var precodeAddr = original.MethodHandle.GetFunctionPointer();
        var replFp = replacement.MethodHandle.GetFunctionPointer();

        // Step 1: read 6 bytes and verify FF 25 signature.
        byte[] header = new byte[6];
        try
        {
            Marshal.Copy(precodeAddr, header, 0, 6);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[JmpHook.InstallIndirect] {label}: failed to read precode bytes: {ex.Message}");
            return false;
        }

        if (header[0] != 0xFF || header[1] != 0x25)
        {
            Console.Error.WriteLine(
                $"[JmpHook.InstallIndirect] {label}: precode does NOT start with FF 25 " +
                $"(got {header[0]:X2} {header[1]:X2}) — wrong dispatch shape, refusing to patch");
            return false;
        }

        // Step 2: RIP-relative displacement at offset 2 (little-endian int32).
        int disp32 = BitConverter.ToInt32(header, 2);

        // Step 3: cell_addr = precode_addr + 6 + disp32 (RIP is at end of the 6-byte instruction).
        long cellAddrRaw = precodeAddr.ToInt64() + 6L + disp32;
        var cellAddr = new IntPtr(cellAddrRaw);

        // Step 4: save original pointer.
        IntPtr originalTarget;
        try
        {
            originalTarget = Marshal.ReadIntPtr(cellAddr);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[JmpHook.InstallIndirect] {label}: failed to read cell at 0x{cellAddrRaw:X}: {ex.Message}");
            return false;
        }

        // Step 5: mprotect the page containing the cell to allow writes.
        //
        // Two cases:
        // (a) Cell is in the SAME page as the precode (code page): disp32 == 0 (happens after
        //     WriteJmp has already patched the precode with an inline FF 25 00 00 00 00 [ptr]).
        //     The cell is at precode+6 in the code page. Need RWX to retain exec permission.
        // (b) Cell is in a DIFFERENT page (FixupPrecode data area, 16 KB+ away): a read-only
        //     data page. Need RW to write; restore to R (no exec — it's data). But we MUST
        //     NOT remove write-ability from adjacent .NET runtime data pages the runtime needs
        //     to update later. Safe approach: open to RW only and restore to RW after write
        //     (leave write permission for the runtime).
        //
        // Distinguish the two cases by checking if cell and precode share the same 4K page.
        long pageSize = 4096;
        long cellPage = cellAddrRaw & ~(pageSize - 1);
        long precodePage = precodeAddr.ToInt64() & ~(pageSize - 1);
        bool cellInCodePage = (cellPage == precodePage);
        var regionSize = (nuint)((cellAddrRaw - cellPage) + 8 + pageSize);

        int restoreProt;
        int writeProt;
        if (cellInCodePage)
        {
            // Code page: need RWX while writing; restore to RX.
            writeProt   = PROT_READ | PROT_WRITE | PROT_EXEC;
            restoreProt = PROT_READ | PROT_EXEC;
        }
        else
        {
            // Data page: open RW; leave as RW so the JIT/runtime can update other cells.
            writeProt   = PROT_READ | PROT_WRITE;
            restoreProt = PROT_READ | PROT_WRITE;
        }

        if (mprotect(new IntPtr(cellPage), regionSize, writeProt) != 0)
        {
            int err = Marshal.GetLastSystemError();
            Console.Error.WriteLine($"[JmpHook.InstallIndirect] {label}: mprotect({(cellInCodePage ? "RWX" : "RW")}) FAILED errno={err}");
            return false;
        }

        // Step 6: atomic 64-bit write via Interlocked.Exchange.
        unsafe
        {
            var cellPtr = (long*)cellAddr.ToPointer();
            System.Threading.Interlocked.Exchange(ref *cellPtr, replFp.ToInt64());
        }

        // Step 7: restore page protection.
        mprotect(new IntPtr(cellPage), regionSize, restoreProt);
        return true;
    }
}
