// JmpHook — patches a JIT'd method's entry point with an x86-64 absolute-indirect JMP
// to a replacement method. Uses mprotect to make the page writable.
//
// .NET 8 lays out method entries as one of three precode shapes; we follow them through
// to the actual JIT'd code so the JMP lands in the right place when the original was
// already JIT-compiled before we hooked it.
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace AlRunner.Infrastructure;

internal static class JmpHook
{
    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(IntPtr addr, nuint len, int prot);
    private const int PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr lpAddress, nuint dwSize, uint flNewProtect, out uint lpflOldProtect);
    private const uint PAGE_NOACCESS = 0x01, PAGE_READONLY = 0x02, PAGE_READWRITE = 0x04,
        PAGE_EXECUTE_READ = 0x20, PAGE_EXECUTE_READWRITE = 0x40;

    private static readonly bool _isWindows =
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>
    /// Cross-platform page-protection change: <c>mprotect</c> on Linux/macOS,
    /// <c>VirtualProtect</c> on Windows (#1650 — verified on real Windows 11/.NET 8;
    /// the same precode shapes WriteJmp/InstallIndirect already handle apply
    /// unchanged, only the OS call that flips the page's RWX bits differs).
    /// Returns 0 on success and nonzero on failure, mirroring mprotect's convention,
    /// so every existing `!= 0` call site keeps working without modification.
    /// </summary>
    private static int ProtectMemory(IntPtr addr, nuint len, int prot)
    {
        if (!_isWindows) return mprotect(addr, len, prot);

        uint winProt = (prot & (PROT_READ | PROT_WRITE | PROT_EXEC)) switch
        {
            PROT_READ | PROT_WRITE | PROT_EXEC => PAGE_EXECUTE_READWRITE,
            PROT_READ | PROT_EXEC => PAGE_EXECUTE_READ,
            PROT_READ | PROT_WRITE => PAGE_READWRITE,
            PROT_READ => PAGE_READONLY,
            _ => PAGE_NOACCESS,
        };
        return VirtualProtect(addr, len, winProt, out _) ? 0 : -1;
    }

    /// <summary>
    /// Byte length for an <c>mprotect</c> call that must cover exactly the page(s) the
    /// <paramref name="bytes"/>-byte write at <paramref name="addr"/> touches — starting from
    /// <paramref name="pageStart"/> (the page base of <paramref name="addr"/>), rounded UP to a
    /// page boundary. It must never extend a full page past the write: doing so re-protects an
    /// adjacent page, and in .NET 8's interleaved code heap that neighbour is frequently live JIT
    /// code — clearing its EXEC bit caused the flaky <c>SEGV_ACCERR</c> Pageworks crash. The old
    /// InstallIndirect form added an unconditional <c>+ pageSize</c>; this is the corrected form
    /// (identical shape to <c>WriteJmp</c>). Pure function so the invariant is unit-testable.
    /// </summary>
    internal static nuint PageRoundedRegionSize(long addr, long pageStart, int bytes, long pageSize)
        => (nuint)(((addr - pageStart) + bytes + pageSize - 1) & ~(pageSize - 1));

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

    // JmpHooks overwrite a JIT'd method's native entry point with an x86-64 JMP. That
    // native-precode parsing is tuned to .NET 10's layout and SEGFAULTS on .NET 8 —
    // which is BC28's REAL runtime (Server.runtimeconfig.json tfm=net8.0). The runner
    // is migrating every hook to the runtime-agnostic Cecil IL-rewrite layer; ~102
    // methods are already Cecil-owned (and auto-skipped here regardless of runtime).
    //
    // DEFAULT: Cecil-only on EVERY runtime (JmpHooks OFF). The native-precode layer is
    // the runtime-fragile one we are removing; the runtime-agnostic Cecil IL-rewrite layer
    // is the end state. net8 (BC28's real runtime) and net10 both run Cecil-only identically
    // with no hangs. The trade-off accepted by the maintainer: until the last ~25 hook
    // clusters are migrated (TestPage architectural gap + state-dependent NREs), net10
    // gives up ~25 corpus passes that the legacy JmpHook layer used to cover (1668→1643).
    //
    // Escape hatch (NOT the default): AL_RUNNER_ENABLE_JMPHOOK=1 re-enables the legacy
    // JmpHook layer (only meaningful on net10 — it SEGFAULTS on net8), e.g. to recover
    // those ~25 tests or to A/B while migrating the remaining clusters.
    // AL_RUNNER_NO_JMPHOOK=1 is retained as an explicit synonym for the default (off).
    private static readonly bool _disabled = ComputeDisabled();

    private static bool ComputeDisabled()
    {
        if (Environment.GetEnvironmentVariable("AL_RUNNER_NO_JMPHOOK") == "1") return true;
        if (Environment.GetEnvironmentVariable("AL_RUNNER_ENABLE_JMPHOOK") == "1") return false;
        return true; // Cecil-only everywhere — the JmpHook layer is removed from the default path.
    }

    // Orphaned-hook audit. With the JmpHook layer off (the default), a Hook(...) call site is a
    // silent no-op unless the target has actually been migrated to a Cecil IL rewrite. A patch
    // owned by NEITHER mechanism simply vanishes — BC's unpatched body runs and typically NREs
    // deep inside Ncl with no runner frame on the stack to point back at the missing patch
    // (this is how the Pageworks NavTestPageBase.ALGoToRecord cluster presented). The remaining
    // migration debt is accepted, but it must be MEASURABLE rather than invisible: record every
    // such call site so `AL_RUNNER_HOOK_AUDIT=1` can name them, along with the CecilOwned key
    // the migration has to add.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _orphaned = new();

    /// <summary>Hook call sites owned by neither JmpHook (disabled) nor a Cecil rewrite.</summary>
    public static System.Collections.Generic.IReadOnlyCollection<string> OrphanedHooks
        => _orphaned.Keys.OrderBy(k => k, System.StringComparer.Ordinal).ToArray();

    // Hook call sites for methods ALREADY owned by a Cecil rewrite — provably inert.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _redundant = new();

    /// <summary>Hook call sites that are dead code because Cecil already owns the method.</summary>
    public static System.Collections.Generic.IReadOnlyCollection<string> RedundantHooks
        => _redundant.Keys.OrderBy(k => k, System.StringComparer.Ordinal).ToArray();

    /// <summary>Clears the orphan audit (test seam).</summary>
    public static void ResetOrphanAudit() { _orphaned.Clear(); _redundant.Clear(); }

    private static readonly bool _audit =
        Environment.GetEnvironmentVariable("AL_RUNNER_HOOK_AUDIT") == "1";

    /// <summary>
    /// Writes the orphaned-hook audit to stderr. Call once after patch install. No-op unless
    /// <c>AL_RUNNER_HOOK_AUDIT=1</c> — the debt is known, so this is diagnostics, not a warning
    /// on every run.
    /// </summary>
    public static void ReportOrphanedHooks()
    {
        if (!_audit) return;
        var orphans = OrphanedHooks;
        Console.Error.WriteLine(
            $"[hook-audit] {orphans.Count} registered patch(es) owned by neither JmpHook (disabled) nor Cecil:");
        foreach (var o in orphans) Console.Error.WriteLine($"[hook-audit]   {o}");

        var redundant = RedundantHooks;
        Console.Error.WriteLine(
            $"[hook-audit] {redundant.Count} registration(s) redundant (Cecil already owns the method) — safe to delete:");
        foreach (var r in redundant) Console.Error.WriteLine($"[hook-audit]   REDUNDANT {r}");
        Console.Error.Flush();
    }

    public static void Apply(MethodBase original, MethodInfo replacement, string name)
    {
        LastAttempt = name;
        if (_disabled)
        {
            // Cecil-owned => the method IS patched, just by the other mechanism. Anything else
            // is an orphan: record it (with its Cecil key) instead of vanishing silently.
            string key;
            try { key = NclCecilRewrite.Key(original); } catch { key = "<unresolvable-key>"; }
            if (NclCecilRewrite.CecilOwned.Contains(key))
                _redundant.TryAdd($"{name}  [{key}]", 0);   // dead code: Cecil already owns it
            else
                _orphaned.TryAdd($"{name}  [{key}]", 0);
            if (_trace) TraceLine($"[JmpHook] SKIP (disabled) {name}");
            return;
        }
        // Cecil-owned skip: a method migrated to a Cecil IL rewrite must be owned by
        // EXACTLY ONE mechanism. Installing a JmpHook on top of the Cecil body recreates
        // the coexistence double-dispatch spin. The registry lives in NclCecilRewrite and
        // is compiled in, so it is populated in this (possibly re-exec'd) process even
        // though RewriteNcl does not re-run here.
        if (NclCecilRewrite.CecilOwned.Contains(NclCecilRewrite.Key(original)))
        {
            // Redundant registration: the method is already fully owned by a Cecil rewrite, so
            // this Hook(...) call site is provably inert dead code and can be deleted with zero
            // behaviour change. (Contrast with the ORPHAN set above, which looks equally inert
            // but is a patch that is *supposed* to act and silently does not.)
            _redundant.TryAdd($"{name}  [{NclCecilRewrite.Key(original)}]", 0);
            TraceLine($"[JmpHook] SKIP (Cecil owns) {name}");
            return;
        }
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
        if (ProtectMemory(new IntPtr(pageStart), regionSize, PROT_READ | PROT_WRITE | PROT_EXEC) != 0)
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
        // Cover ONLY the page(s) the 8-byte cell actually spans, rounded up to a page
        // boundary — never an extra full page beyond it. The previous form
        // ((cellAddrRaw - cellPage) + 8 + pageSize) unconditionally extended the mprotect
        // region a whole page PAST the cell, into the following page. In .NET 8's code
        // heap, code and precode/cell data pages interleave at 4 KB granularity, so that
        // trailing page is frequently live JIT code. In the data-page branch below the
        // region is restored to PROT_READ|PROT_WRITE (no EXEC), which stripped the
        // execute bit off that adjacent code page and never restored it — a later
        // `ret`/call into it then SIGSEGV'd with SEGV_ACCERR (flaky, JIT-layout dependent:
        // the Pageworks CU50364 ~80% native crash). WriteJmp already rounds this way;
        // mirror it here.
        var regionSize = PageRoundedRegionSize(cellAddrRaw, cellPage, 8, pageSize);

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

        if (ProtectMemory(new IntPtr(cellPage), regionSize, writeProt) != 0)
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
        ProtectMemory(new IntPtr(cellPage), regionSize, restoreProt);
        return true;
    }
}
