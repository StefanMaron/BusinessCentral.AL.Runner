using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// A loop inside a catch, filter or finally block makes the JIT compile the WHOLE method
/// "Tier-0 switched to FullOpts" (OSR cannot place a patchpoint in a handler). For
/// <c>&lt;Main&gt;$</c>, 33 KB of IL, that cost about 0.26G instructions in every process
/// generation of every invocation, the shadow re-exec parent and its child alike (#2375).
/// See docs/startup-cost.md#main-jit-tier.
/// </summary>
public sealed class HandlerLoopJitTierGuardTests
{
    private static readonly string RunnerAssemblyPath = typeof(AlRunner.BcRuntime).Assembly.Location;
    private static readonly string BuiltRunnerPath = Path.Combine(
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..")),
        "AlRunner", "bin", TestBuildConfig.Configuration, TestBuildConfig.Framework, "al-runner.dll");

    [Fact]
    public void NoRunnerMethod_BranchesBackwardInsideAnExceptionHandler()
    {
        var scan = HandlerBackBranchScanner.Scan(RunnerAssemblyPath);

        // Third state: a scan that decoded nothing, or never reached Main, must not read as a pass.
        Assert.True(scan.MethodsDecoded > 1000, $"decoded only {scan.MethodsDecoded} method bodies in {RunnerAssemblyPath}");
        Assert.True(scan.BackBranchesByMethod.TryGetValue("Program::<Main>$", out var mainLoops),
            "Program::<Main>$ was not found in " + RunnerAssemblyPath);
        Assert.True(mainLoops > 10, $"Program::<Main>$ decoded with {mainLoops} backward branches; the decoder is not reading branches");

        Assert.True(scan.Offenders.Count == 0,
            "Backward branch inside an exception handler (forces the JIT to compile the whole method " +
            "FullOpts; move the loop into a helper method, see docs/startup-cost.md#main-jit-tier):\n  " +
            string.Join("\n  ", scan.Offenders));
    }

    [Fact]
    public void Scanner_FlagsLoopsInHandlers_AndNotALeaveBackToTheLoopHead()
    {
        var scan = HandlerBackBranchScanner.Scan(typeof(HandlerLoopJitTierGuardTests).Assembly.Location);
        string Name(string m) => $"{nameof(HandlerLoopShapes)}::{m}";

        Assert.Contains(scan.Offenders, o => o.StartsWith(Name(nameof(HandlerLoopShapes.LoopInFinally)) + " "));
        Assert.Contains(scan.Offenders, o => o.StartsWith(Name(nameof(HandlerLoopShapes.LoopInCatch)) + " "));
        Assert.Contains(scan.Offenders, o => o.StartsWith(Name(nameof(HandlerLoopShapes.LoopInFilteredCatch)) + " "));
        // A `leave` out of a catch back to an enclosing loop's head does not force FullOpts
        // (measured: Instrumented Tier0), so it must not be reported.
        Assert.DoesNotContain(scan.Offenders, o => o.StartsWith(Name(nameof(HandlerLoopShapes.TryCatchInsideLoop)) + " "));
        Assert.True(scan.LeaveBackBranchesByMethod.GetValueOrDefault(Name(nameof(HandlerLoopShapes.TryCatchInsideLoop))) > 0,
            "the TryCatchInsideLoop fixture no longer compiles to a leave back to the loop head, so it tests nothing");
    }

    /// <summary>
    /// The observable itself, on the one path where <c>&lt;Main&gt;$</c> is the only large method
    /// compiled: the JIT's own summary line for it must not say "switched to FullOpts".
    /// </summary>
    [SkippableFact]
    public void VersionFastPath_MainIsNotCompiledFullOpts()
    {
        var debuggable = typeof(AlRunner.BcRuntime).Assembly.GetCustomAttribute<DebuggableAttribute>();
        Skip.If(debuggable?.IsJITOptimizerDisabled == true,
            "al-runner.dll was built with the JIT optimizer disabled (Debug), so every method is MinOpts and the tier says nothing");

        Assert.True(File.Exists(BuiltRunnerPath), "runner not built at " + BuiltRunnerPath);
        var jitLog = Path.Combine(Directory.CreateDirectory(TestScratch.FlatDir("al-runner-jit-tier-")).FullName, "jit.txt");
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(BuiltRunnerPath);
        psi.ArgumentList.Add("--version");
        psi.Environment["DOTNET_JitDisasmSummary"] = "1";
        psi.Environment["DOTNET_JitStdOutFile"] = jitLog;
        using (var p = Process.Start(psi)!)
        {
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            Assert.True(p.WaitForExit(120_000), "al-runner --version did not exit within 120s");
            Assert.Equal(0, p.ExitCode);
        }

        Assert.True(File.Exists(jitLog), "DOTNET_JitStdOutFile produced no file; the JIT summary could not be read");
        // More than one line is legitimate (an OSR recompile of a hot loop adds one).
        var mainLines = File.ReadLines(jitLog).Where(l => l.Contains("Program:<Main>$(")).ToList();
        Assert.True(mainLines.Count > 0, "no JIT summary line for Program:<Main>$ in " + jitLog);
        Assert.DoesNotContain(mainLines, l => l.Contains("switched to FullOpts"));
        Assert.Contains(mainLines, l => l.Contains("Tier0"));
    }
}

/// <summary>Fixture shapes for the scanner's own positive and negative cases.</summary>
internal static class HandlerLoopShapes
{
    internal static int LoopInFinally(int[] xs)
    {
        int s = 0;
        try { s = xs.Length; }
        finally { foreach (var x in xs) s += x; }
        return s;
    }

    internal static int LoopInCatch(int[] xs)
    {
        int s = 0;
        try { s = xs[xs.Length]; }
        catch (IndexOutOfRangeException) { for (int i = 0; i < xs.Length; i++) s += xs[i]; }
        return s;
    }

    internal static int LoopInFilteredCatch(int[] xs)
    {
        int s = 0;
        try { s = xs[xs.Length]; }
        catch (Exception e) when (e is IndexOutOfRangeException) { foreach (var x in xs) s += x; }
        return s;
    }

    internal static int TryCatchInsideLoop(int[] xs)
    {
        int i = 0, s = 0;
        while (true)
        {
            i++;
            if (i > xs.Length) break;
            try { s += 10 / xs[i - 1]; }
            catch (DivideByZeroException) { s--; }
        }
        return s;
    }
}

internal static class HandlerBackBranchScanner
{
    internal sealed record Result(int MethodsDecoded, IReadOnlyList<string> Offenders,
        IReadOnlyDictionary<string, int> BackBranchesByMethod, IReadOnlyDictionary<string, int> LeaveBackBranchesByMethod);

    private static readonly Dictionary<byte, OpCode> OneByte = new();
    private static readonly Dictionary<byte, OpCode> TwoByte = new();

    static HandlerBackBranchScanner()
    {
        foreach (var f in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            var op = (OpCode)f.GetValue(null)!;
            if (op.Size == 1) OneByte[(byte)op.Value] = op;
            else TwoByte[(byte)(op.Value & 0xFF)] = op;
        }
    }

    internal static Result Scan(string assemblyPath)
    {
        using var pe = new PEReader(File.OpenRead(assemblyPath));
        var md = pe.GetMetadataReader();
        var offenders = new List<string>();
        var backBranches = new Dictionary<string, int>();
        var leaveBacks = new Dictionary<string, int>();
        int decoded = 0;

        foreach (var handle in md.MethodDefinitions)
        {
            var method = md.GetMethodDefinition(handle);
            if (method.RelativeVirtualAddress == 0) continue;
            var name = $"{md.GetString(md.GetTypeDefinition(method.GetDeclaringType()).Name)}::{md.GetString(method.Name)}";
            var body = pe.GetMethodBody(method.RelativeVirtualAddress);
            var il = body.GetILBytes() ?? throw new InvalidOperationException("no IL for " + name);
            decoded++;

            var handlerRanges = body.ExceptionRegions
                .Select(r => (Start: r.Kind == ExceptionRegionKind.Filter ? r.FilterOffset : r.HandlerOffset,
                              End: r.HandlerOffset + r.HandlerLength))
                .ToList();

            int backs = 0;
            var sites = new List<string>();
            int i = 0;
            while (i < il.Length)
            {
                int start = i;
                OpCode op;
                if (il[i] == 0xFE)
                {
                    if (i + 1 >= il.Length || !TwoByte.TryGetValue(il[i + 1], out op))
                        throw new InvalidOperationException($"{name}: undecodable opcode at IL_{start:x4}");
                    i += 2;
                }
                else if (!OneByte.TryGetValue(il[i], out op))
                    throw new InvalidOperationException($"{name}: undecodable opcode at IL_{start:x4}");
                else
                    i += 1;

                long target = -1;
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

                if (target < 0 || target > start) continue;
                if (op == OpCodes.Leave || op == OpCodes.Leave_S)
                {
                    leaveBacks[name] = leaveBacks.GetValueOrDefault(name) + 1;
                    continue;
                }
                backs++;
                if (handlerRanges.Any(h => start >= h.Start && start < h.End))
                    sites.Add($"IL_{start:x4}->IL_{target:x4}");
            }

            backBranches[name] = backBranches.GetValueOrDefault(name) + backs;
            if (sites.Count > 0)
                offenders.Add($"{name} (IL {il.Length} bytes): {string.Join(", ", sites)}");
        }

        return new Result(decoded, offenders, backBranches, leaveBacks);
    }
}
