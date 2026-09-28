using System.Reflection;
using System.Runtime.CompilerServices;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// The two probes differ only in whether the early return sits before or inside the try, so the
// helper must count 1 and 0 respectively in whichever configuration this assembly was built.
public sealed class IlEarlyExitsTests
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? ReturnsBeforeTry(int x)
    {
        if (x == 0) return null;
        try { return x.ToString(); }
        catch (Exception) when (x > 1) { return null; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object? ReturnsInsideTry(int x)
    {
        try
        {
            if (x == 0) return null;
            return x.ToString();
        }
        catch (Exception) when (x > 1) { return null; }
    }

    private static List<int> Exits(string name)
    {
        var body = typeof(IlEarlyExitsTests)
            .GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetMethodBody()!;
        var tryOffset = body.ExceptionHandlingClauses.Min(c => c.TryOffset);
        return IlEarlyExits.ExitSitesBefore(body, tryOffset);
    }

    [Fact]
    public void AReturnAheadOfTheTry_IsOneExit()
        => Assert.Single(Exits(nameof(ReturnsBeforeTry)));

    [Fact]
    public void AReturnInsideTheTry_IsNoExitBeforeIt()
        => Assert.Empty(Exits(nameof(ReturnsInsideTry)));

    [Fact]
    public void EveryRecordPatchesBody_DecodesToItsExactLength()
    {
        // Decode throws on an unknown opcode or a body it overran, so a mis-sized operand anywhere
        // in the opcode table surfaces here rather than as a mis-framed exit count.
        int decoded = 0;
        foreach (var m in typeof(RecordPatches).GetMethods(
                     BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static
                     | BindingFlags.Instance | BindingFlags.DeclaredOnly))
        {
            var il = m.GetMethodBody()?.GetILAsByteArray();
            if (il == null) continue;
            IlEarlyExits.Decode(il);
            decoded++;
        }
        Assert.True(decoded > 100, $"only {decoded} RecordPatches bodies decoded");
    }
}
