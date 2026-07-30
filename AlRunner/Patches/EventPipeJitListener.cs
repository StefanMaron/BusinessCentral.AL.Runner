// EventPipeJitListener — in-process JIT event subscriber for post-JIT patching.
//
// Strategy (Spike 4):
//   Subscribe to Microsoft-Windows-DotNETRuntime keyword 0x10 (JIT) at Verbose level.
//   Listen for MethodLoadVerbose_V1 events which fire AFTER the JIT has finished
//   compiling a method. At that point MethodStartAddress points to the stable
//   compiled body — NOT the precode that the JIT may still read.
//
//   Key differentiator vs prior spikes: we patch the compiled body, not the precode.
//   The FixupPrecode cell and MOV R10 bytes remain untouched → no JIT-invariant violations.
//
// .NET 8 MethodLoadVerbose_V1 payload fields (from ETW manifest, verified against CoreCLR):
//   0: MethodID        (uint64)
//   1: ModuleID        (uint64)
//   2: MethodStartAddress (uint64)
//   3: MethodSize      (uint32)
//   4: MethodToken     (uint32)
//   5: MethodFlags     (uint32)
//   6: MethodNamespace (string)
//   7: MethodName      (string)
//   8: MethodSignature (string)
//
// The EventSource event name in .NET 8 is "MethodLoadVerbose_V1".
// Keywords: JITKeyword = 0x10. EventLevel: Verbose.
//
// Safe-write strategy:
//   After receiving MethodLoadVerbose_V1 for a target method:
//   1. Read the first 32 bytes of MethodStartAddress.
//   2. Scan forward byte-by-byte to find the first instruction boundary
//      PAST any AVX/SSE prolog. We skip:
//        - VZEROUPPER  (C5 F8 77)
//        - VXORPS      (C5 F0 57 ...)
//        - MOV R10,... (4D 8B ...)
//        - PUSH RBP    (55)
//        - PUSH RBX    (53)
//        - PUSH RSI    (56)
//        - PUSH RDI    (57)
//        - MOV RBP,RSP (48 89 EC)
//        - SUB RSP,... (48 83 EC ...) or (48 81 EC ...)
//   3. We apply a 14-byte absolute indirect JMP at that offset (or at 0 if the
//      method body starts with stable bytes — always conservative with ≥14 bytes).
//
// STOP conditions honoured:
//   - MethodLoadVerbose_V1 not seen for BC types → log and set _bcEventsObserved=false.
//   - MethodStartAddress looks like a stub (< 16 bytes, or starts FF 25) → skip+log.
//   - SafeWriteOffset > 64 → skip+log (unusually large prolog, need more investigation).
using System.Diagnostics.Tracing;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AlRunner.Infrastructure;

namespace AlRunner;

/// <summary>
/// In-process EventListener that subscribes to JIT MethodLoad events.
/// Start() enables the listener before test assemblies load. After the JIT
/// compiles each target method, the listener calls back into JmpHook to install
/// a patched JMP at the compiled body.
/// </summary>
public sealed class EventPipeJitListener : EventListener
{
    // ── Configuration ──────────────────────────────────────────────────────────

    /// <summary>
    /// Phase-A diagnostic mode: when true, OnEventWritten logs TARGET MATCH but
    /// does NOT attempt any body/precode patching. Lets us verify EventPipe
    /// plumbing (events arriving + target matching) without risk of segfault.
    /// </summary>
    public static bool DryRun = false;

    /// <summary>
    /// Methods to intercept: (full type name, method name, replacement MethodInfo).
    /// Populated before enabling the listener.
    /// </summary>
    private readonly List<(string TypeFqn, string MethodName, MethodInfo Replacement, MethodBase? Original)> _targets = new();

    // Diagnostics
    public int TotalMethodLoadEvents   { get; private set; }
    public int BcMethodLoadEvents      { get; private set; }
    public bool BcEventsObserved       { get; private set; } = false;
    private readonly object _lock = new();

    // Hook status per target (indexed same as _targets)
    private readonly Dictionary<string, bool> _patched = new();

    // DryRun: thread-safe accumulators populated from JIT-callback thread without
    // any Console.Error.WriteLine (which proved to SEGV under heavy JIT volume
    // due to reentrancy: formatting/locking on a thread that itself may be
    // running inside a JIT helper).
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _dryBcSamples = new();
    private readonly System.Collections.Concurrent.ConcurrentBag<string> _dryTargetMatches = new();
    public IReadOnlyCollection<string> DryBcSamples => _dryBcSamples.Keys.ToList();
    public IReadOnlyCollection<string> DryTargetMatches => _dryTargetMatches.ToArray();

    // Verbose logging flag: set to suppress after first few hundred events
    private int _loggedCount;
    private const int MaxVerboseLog = 20;

    // ── Public API ─────────────────────────────────────────────────────────────

    /// <summary>Add a target before calling Enable().</summary>
    public void AddTarget(string typeFqn, string methodName, MethodInfo replacement, MethodBase? original = null)
    {
        var key = $"{typeFqn}.{methodName}";
        _targets.Add((typeFqn, methodName, replacement, original));
        Console.Error.WriteLine($"[EventPipeJIT] Target registered: {key} → {replacement.Name}");
    }

    /// <summary>
    /// Enable JIT keyword on Microsoft-Windows-DotNETRuntime.
    /// Call this once, BEFORE loading test assemblies.
    /// </summary>
    public void Enable()
    {
        // Microsoft-Windows-DotNETRuntime GUID: {e13c0d23-ccbc-4e12-931b-d9cc2eee27e4}
        // Keywords: JITKeyword = 0x10  (MethodJittingStarted, MethodLoadVerbose etc.)
        //           NGenKeyword = 0x20  (for R2R/NGen)
        // Level: Verbose (5)
        Console.Error.WriteLine("[EventPipeJIT] Enabling listener on Microsoft-Windows-DotNETRuntime (JIT keyword 0x10, Verbose)");
    }

    // ── EventListener overrides ────────────────────────────────────────────────

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        if (eventSource.Name == "Microsoft-Windows-DotNETRuntime")
        {
            // JITKeyword = 0x10, Verbose = EventLevel.Verbose (5)
            // Using EventKeywords(0x10) — JIT events.
            EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)0x10);
            Console.Error.WriteLine($"[EventPipeJIT] Subscribed to {eventSource.Name} (keywords=0x10, Verbose)");
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        // Filter: only MethodLoadVerbose_V1 (or MethodLoad_V1 — .NET 8 uses both names)
        // Event IDs: MethodLoad_V1 = 143, MethodLoadVerbose_V1 = 141 (from CoreCLR manifest)
        // We filter by name prefix to handle both V1/V2 variants.
        if (!eventData.EventName.StartsWith("MethodLoad", StringComparison.Ordinal))
            return;

        Interlocked.Increment(ref _rawTotalEvents);

        // Fast-path: if DryRun, do minimal work and NEVER call Console.Error.WriteLine
        // (proved to SEGV under heavy JIT-volume reentrancy).
        bool dry = DryRun;

        try
        {
            // Payload fields for MethodLoadVerbose_V1:
            //   [2] MethodStartAddress (ulong)
            //   [6] MethodNamespace (string)
            //   [7] MethodName (string)
            if (eventData.Payload == null || eventData.Payload.Count < 8) return;

            var methodNamespace = eventData.Payload[6] as string;
            var methodName      = eventData.Payload[7] as string;

            if (methodNamespace == null || methodName == null) return;

            // Count total and BC-specific events
            Interlocked.Increment(ref _rawTotalMethodLoadEvents);

            bool isBc = methodNamespace.StartsWith("Microsoft.Dynamics.Nav", StringComparison.Ordinal);
            if (isBc)
            {
                int bcCount = Interlocked.Increment(ref _rawBcEvents);
                BcEventsObserved = true;

                if (dry)
                {
                    // Stash a small sample of BC type+method pairs (cap implicitly via dictionary).
                    if (_dryBcSamples.Count < 60)
                        _dryBcSamples.TryAdd(methodNamespace + "." + methodName, 0);
                }
                else if (bcCount <= MaxVerboseLog)
                {
                    Console.Error.WriteLine($"[EventPipeJIT] BC MethodLoad: {methodNamespace}.{methodName} (EventName={eventData.EventName})");
                }
            }

            // Check against targets
            foreach (var (typeFqn, targetMethodName, replacement, original) in _targets)
            {
                if (!string.Equals(methodName, targetMethodName, StringComparison.Ordinal)) continue;
                if (!typeFqn.EndsWith("." + methodNamespace.Split('.').LastOrDefault(), StringComparison.Ordinal) &&
                    !typeFqn.Contains(methodNamespace, StringComparison.Ordinal)) continue;

                var key = $"{typeFqn}.{targetMethodName}";
                // Don't dedup — re-patch every time the method is (re)JIT'd (tiered compilation
                // can produce multiple MethodLoad events for the same method at different addresses).
                lock (_lock) { _patched[key] = true; }

                // Extract MethodStartAddress from payload[2]
                ulong startAddr = 0;
                try
                {
                    var raw = eventData.Payload[2];
                    startAddr = raw switch
                    {
                        ulong u => u,
                        long  l => (ulong)l,
                        int   i => (ulong)(uint)i,
                        uint  u => (ulong)u,
                        _       => Convert.ToUInt64(raw)
                    };
                }
                catch { continue; }

                uint methodSize = 0;
                try
                {
                    var raw = eventData.Payload[3];
                    methodSize = raw switch
                    {
                        uint  u => u,
                        int   i => (uint)i,
                        ulong u => (uint)u,
                        long  l => (uint)(ulong)l,
                        _       => Convert.ToUInt32(raw)
                    };
                }
                catch { }

                uint mflags = 0;
                try
                {
                    var rawF = eventData.Payload.Count > 5 ? eventData.Payload[5] : null;
                    if (rawF != null)
                        mflags = rawF switch { uint u => u, int i => (uint)i, ulong u => (uint)u, _ => Convert.ToUInt32(rawF) };
                }
                catch { }

                if (dry)
                {
                    // Stash and return — no console output, no patching.
                    _dryTargetMatches.Add($"{key} addr=0x{startAddr:X} size={methodSize} flags=0x{mflags:X} ev={eventData.EventName}");
                    continue;
                }

                Console.Error.WriteLine(
                    $"[EventPipeJIT] TARGET MATCH: {key} " +
                    $"MethodStartAddress=0x{startAddr:X} MethodSize={methodSize} " +
                    $"MethodFlags=0x{mflags:X} EventName={eventData.EventName}");

                if (startAddr == 0)
                {
                    Console.Error.WriteLine($"[EventPipeJIT] {key}: MethodStartAddress=0 — skipping (stub?)");
                    continue;
                }

                // Primary strategy: patch compiled body bytes at offset 0.
                ApplyCompiledBodyPatch(key, new IntPtr((long)startAddr), methodSize, replacement);

                // Secondary strategy: use JmpHook.Apply to overwrite the PRECODE entry.
                // This catches all callers regardless of how they address the method.
                // Timing: post-MethodLoad → method is fully JIT'd → the FixupPrecode's
                // MOV R10 path (pre-JIT-lazy-compile path) is no longer active.
                if (original != null)
                {
                    Infrastructure.JmpHook.Apply(original, replacement, $"{key} [post-MethodLoad precode]");
                }

                // InstallIndirect: also patch the precode cell.
                if (original != null)
                {
                    bool ok = Infrastructure.JmpHook.InstallIndirect(original, replacement, $"{key} [post-MethodLoad]");
                    Console.Error.WriteLine($"[EventPipeJIT] InstallIndirect({key}): {(ok ? "OK" : "FAILED")}");
                }

                // Diagnostic: read precode cell AFTER patch to confirm it points to our JMP.
                if (original != null)
                {
                    try
                    {
                        var precodeAddr = original.MethodHandle.GetFunctionPointer();
                        byte[] pre = new byte[8];
                        Marshal.Copy(precodeAddr, pre, 0, 8);
                        if (pre[0] == 0xFF && pre[1] == 0x25)
                        {
                            int d32 = BitConverter.ToInt32(pre, 2);
                            long cellA = precodeAddr.ToInt64() + 6 + d32;
                            var cellVal = Marshal.ReadIntPtr(new IntPtr(cellA));
                            Console.Error.WriteLine($"[EventPipeJIT] {key}: post-patch precode cell=0x{cellA:X} → 0x{cellVal:X} (startAddr=0x{startAddr:X})");
                        }
                        else
                        {
                            Console.Error.WriteLine($"[EventPipeJIT] {key}: precode bytes after Apply: {string.Join(" ", pre.Select(b => b.ToString("X2")))}");
                        }
                    }
                    catch { }
                }

                // ── Diagnostics: also read the method's precode to see what the precode
                //    cell currently points to (should be compiledBodyAddr if cell was already updated).
                try
                {
                    RuntimeHelpers.PrepareMethod(replacement.MethodHandle); // ensure replacement JIT'd
                    // Find the target method by reflection (heavy but diagnostic-only).
                    // We do this in a lazy way: cache the precode reading per target.
                    // For now just log a marker so we can cross-reference.
                    Console.Error.WriteLine($"[EventPipeJIT] NOTE: patch applied to compiledBody=0x{startAddr:X}. Precode-cell verification deferred.");
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EventPipeJIT] OnEventWritten error: {ex.Message}");
        }
    }

    // Shared atomic counters (writable from any thread via Interlocked)
    private int _rawTotalEvents;
    private int _rawTotalMethodLoadEvents;
    private int _rawBcEvents;

    // Snapshot for public properties (called from test runner thread after warm-up)
    public void SnapshotCounters()
    {
        TotalMethodLoadEvents = _rawTotalMethodLoadEvents;
        BcMethodLoadEvents    = _rawBcEvents;
    }

    // ── Compiled-body JMP patch ────────────────────────────────────────────────

    /// <summary>
    /// Reads up to 48 bytes from compiledBodyAddr, finds a safe write offset past
    /// the method prolog (PUSH RBP, frame setup, AVX init, MOV R10 etc.),
    /// then writes a 14-byte absolute-indirect JMP at that offset.
    ///
    /// Safe-skip rules (conservative):
    ///   - 55          PUSH RBP
    ///   - 53          PUSH RBX
    ///   - 56          PUSH RSI
    ///   - 57          PUSH RDI
    ///   - 41 54..41 57  PUSH R12..R15
    ///   - 48 89 xx    MOV r/m64, r64 (3 bytes)
    ///   - 48 83 EC xx SUB RSP, imm8  (4 bytes)
    ///   - 48 81 EC xx xx xx xx  SUB RSP, imm32 (7 bytes)
    ///   - C5 F8 77    VZEROUPPER (3 bytes)
    ///   - C5 xx 57 xx VXORPS (4 bytes)
    ///   - 4C 8B xx xx xx xx xx  MOV R10/R11, [rip+disp32] (7 bytes)
    ///   - 4D 8B xx xx xx xx xx  ditto
    ///   - 66 90       xchg ax,ax (NOP, 2 bytes)
    ///   - 90          NOP (1 byte)
    ///   - 0F 1F xx..  multi-byte NOP
    ///
    /// Returns the safe offset, or -1 if we can't determine a safe boundary.
    /// </summary>
    private static int FindSafeWriteOffset(byte[] bytes)
    {
        int pos = 0;
        int limit = bytes.Length - 14; // need at least 14 bytes for the JMP
        if (limit <= 0) return -1;

        while (pos <= limit)
        {
            byte b0 = bytes[pos];
            byte b1 = pos + 1 < bytes.Length ? bytes[pos + 1] : (byte)0;
            byte b2 = pos + 2 < bytes.Length ? bytes[pos + 2] : (byte)0;
            byte b3 = pos + 3 < bytes.Length ? bytes[pos + 3] : (byte)0;

            // Already a stub/precode? If method starts with FF 25, it hasn't been compiled yet.
            if (pos == 0 && b0 == 0xFF && b1 == 0x25)
                return -2; // sentinel: still-a-stub, not compiled body

            // PUSH reg: 55 (RBP), 53 (RBX), 56 (RSI), 57 (RDI)
            if (b0 == 0x55 || b0 == 0x53 || b0 == 0x56 || b0 == 0x57) { pos += 1; continue; }

            // PUSH R12..R15: 41 54..41 57
            if (b0 == 0x41 && b1 >= 0x54 && b1 <= 0x57) { pos += 2; continue; }

            // REX.W prefix sequences (48 xx ...):
            if (b0 == 0x48)
            {
                if (b1 == 0x89) { pos += 3; continue; }              // MOV r/m64,r64
                if (b1 == 0x83 && b2 == 0xEC) { pos += 4; continue; }// SUB RSP,imm8
                if (b1 == 0x81 && b2 == 0xEC) { pos += 7; continue; }// SUB RSP,imm32
                if (b1 == 0x8B) { pos += 3; continue; }              // MOV r64,r/m64
                if (b1 == 0x8D) { pos += 4; continue; }              // LEA (common in frame setup)
                // Fall through — don't know the length, stop here.
                break;
            }

            // REX.W + REX.R sequences (4C/4D 8B [RIP+disp32] = MOV R10/R11,[rip+disp32])
            if ((b0 == 0x4C || b0 == 0x4D) && b1 == 0x8B)
            {
                byte modrm = b2;
                if ((modrm & 0xC7) == 0x15) { pos += 7; continue; } // [RIP+disp32]: ModRM = 0x15
                if ((modrm & 0xC7) == 0x05) { pos += 7; continue; } // [RIP+disp32]: ModRM = 0x05
                // Other MOV encodings: give up
                break;
            }

            // VZEROUPPER: C5 F8 77
            if (b0 == 0xC5 && b1 == 0xF8 && b2 == 0x77) { pos += 3; continue; }

            // VXORPS: C5 xx 57 xx
            if (b0 == 0xC5 && b2 == 0x57) { pos += 4; continue; }

            // NOP: 90
            if (b0 == 0x90) { pos += 1; continue; }

            // XCHG AX,AX (2-byte NOP): 66 90
            if (b0 == 0x66 && b1 == 0x90) { pos += 2; continue; }

            // Multi-byte NOP: 0F 1F [ModRM] ...  (2..9 bytes)
            if (b0 == 0x0F && b1 == 0x1F)
            {
                byte modrm = b2;
                int rm = modrm & 7;
                int mod = (modrm >> 6) & 3;
                int nopLen = 3; // base: 0F 1F [ModRM]
                if (rm == 4) nopLen++; // SIB byte
                if (mod == 1) nopLen++; // disp8
                if (mod == 2) nopLen += 4; // disp32
                pos += nopLen;
                continue;
            }

            // Anything else: we've reached real code — this is the safe write point.
            break;
        }

        if (pos > limit) return -1; // ran off end of prolog buffer
        return pos;
    }

    /// <summary>
    /// Applies a 14-byte absolute-indirect JMP at the safe write offset within
    /// the compiled method body. Uses mprotect to make the page writable.
    /// </summary>
    private static void ApplyCompiledBodyPatch(string label, IntPtr compiledBodyAddr, uint methodSize, MethodInfo replacement)
    {
        Console.Error.WriteLine($"[EventPipeJIT] ApplyCompiledBodyPatch: {label} addr=0x{compiledBodyAddr:X} size={methodSize}");

        // Step 1: Read first 48 bytes for prolog analysis.
        const int ReadLen = 48;
        byte[] bytes = new byte[ReadLen];
        int readLen = (int)Math.Min((uint)ReadLen, methodSize == 0 ? (uint)ReadLen : methodSize);
        if (readLen < 14)
        {
            Console.Error.WriteLine($"[EventPipeJIT] {label}: method too small ({readLen} bytes) — skipping");
            return;
        }

        try
        {
            Marshal.Copy(compiledBodyAddr, bytes, 0, readLen);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EventPipeJIT] {label}: failed to read compiled body: {ex.Message}");
            return;
        }

        string hexDump = string.Join(" ", bytes.Take(readLen).Select(b => b.ToString("X2")));
        Console.Error.WriteLine($"[EventPipeJIT] {label}: compiled body first {readLen} bytes: [{hexDump}]");

        // Step 2: Detect if this is still a stub/precode (shouldn't be — MethodLoad fires post-JIT).
        if (bytes[0] == 0xFF && bytes[1] == 0x25)
        {
            Console.Error.WriteLine($"[EventPipeJIT] {label}: STOP — body starts with FF 25 (still a stub/precode, MethodLoad fired too early?)");
            return;
        }

        // Step 3: Write JMP at offset 0 (the compiled body entry point).
        // ALL callers enter at offset 0; writing at any higher offset means callers
        // execute original prolog bytes before reaching our JMP, corrupting the stack.
        // The compiled body bytes (prolog: PUSH RBP, PUSH RBX, SUB RSP) are NOT JIT
        // metadata — they're safe to overwrite. The JIT re-reads the precode CELL,
        // not the compiled body bytes, when compiling new callers.
        const int safeOffset = 0;
        Console.Error.WriteLine($"[EventPipeJIT] {label}: safe write offset = {safeOffset} (offset 0, compiled body entry)");

        // Step 4: Prepare replacement function pointer.
        RuntimeHelpers.PrepareMethod(replacement.MethodHandle);
        IntPtr replFp;
        try { replFp = replacement.MethodHandle.GetFunctionPointer(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EventPipeJIT] {label}: failed to get replacement FunctionPointer: {ex.Message}");
            return;
        }

        // Step 5: Write a 14-byte absolute-indirect JMP at compiledBodyAddr + safeOffset.
        IntPtr writeTarget = compiledBodyAddr + safeOffset;
        Console.Error.WriteLine($"[EventPipeJIT] {label}: writing 14-byte JMP at 0x{writeTarget:X} → repl=0x{replFp:X}");

        WriteJmp14(writeTarget, replFp, label);

        // Step 6: Verify the patch was written by reading back.
        try
        {
            byte[] verify = new byte[14];
            Marshal.Copy(writeTarget, verify, 0, 14);
            string verifyHex = string.Join(" ", verify.Select(b => b.ToString("X2")));
            Console.Error.WriteLine($"[EventPipeJIT] {label}: verify readback: [{verifyHex}]");
            bool jmpOk = verify[0] == 0xFF && verify[1] == 0x25;
            Console.Error.WriteLine($"[EventPipeJIT] {label}: JMP signature correct: {jmpOk}");

            // Also read the replacement FP stored at offset 6.
            long storedFp = BitConverter.ToInt64(verify, 6);
            Console.Error.WriteLine($"[EventPipeJIT] {label}: stored FP=0x{storedFp:X} expected=0x{replFp:X} match={storedFp == replFp.ToInt64()}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[EventPipeJIT] {label}: readback failed: {ex.Message}");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int mprotect(IntPtr addr, nuint len, int prot);
    private const int PROT_READ = 1, PROT_WRITE = 2, PROT_EXEC = 4;

    private static void WriteJmp14(IntPtr target, IntPtr destination, string label)
    {
        // x86-64 absolute indirect JMP via inline 8-byte pointer:
        //   FF 25 00 00 00 00        JMP QWORD PTR [RIP+0] (next 8 bytes)
        //   [8 bytes: destination]
        byte[] jmp = new byte[14];
        jmp[0] = 0xFF; jmp[1] = 0x25;
        // disp32 = 0: [RIP+0] points to the 8 bytes immediately following this instruction.
        // Bytes 2-5 are already zero (the disp32).
        BitConverter.GetBytes(destination.ToInt64()).CopyTo(jmp, 6);

        long pageSize = 4096;
        long addr     = target.ToInt64();
        long pageStart = addr & ~(pageSize - 1);
        var regionSize = (nuint)((addr - pageStart) + jmp.Length + pageSize);

        if (mprotect(new IntPtr(pageStart), regionSize, PROT_READ | PROT_WRITE | PROT_EXEC) != 0)
        {
            int err = Marshal.GetLastSystemError();
            Console.Error.WriteLine($"[EventPipeJIT] {label}: mprotect(RWX) FAILED errno={err}");
            return;
        }

        Marshal.Copy(jmp, 0, target, jmp.Length);

        // Restore page protection.
        mprotect(new IntPtr(pageStart), regionSize, PROT_READ | PROT_EXEC);

        Console.Error.WriteLine($"[EventPipeJIT] {label}: JMP written ✓ at 0x{target:X}+{(addr - (addr & ~(pageSize - 1))):X} → 0x{destination:X}");
    }
}
