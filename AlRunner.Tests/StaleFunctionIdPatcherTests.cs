// Tests for BcAssembler.ApplyStaleFunctionIdPatches and the StaleSymbolUpgrader
// registry that feeds it.
//
// Root cause being tested
// -----------------------
// ISV .alpackages dirs sometimes contain stale Microsoft symbol-only .app files
// (e.g. Microsoft_Tests-TestLibraries.app v17.0). BC's compiler reads these and
// bakes the old function IDs as integer literals into emitted C#:
//
//     await this.Target.Invoke(-1033141710, new object[] { this.vendor });
//                              ^^^^^^^^^^^^  ← stale v17 ID for CreateVendor
//
// At runtime, BC 28.1's dispatch switch only knows the current ID (266061949) →
// NavNCLCompilationException: "Function ID -1033141710 was called. The object
// with ID 130512 does not have a member with that ID."
//
// Fix: StaleSymbolUpgrader.TryRegisterIds populates a global stale→current map;
// BcAssembler.ApplyStaleFunctionIdPatches regex-replaces those literals before
// Roslyn compiles the C#.
//
// Test strategy
// -------------
// These are WHITE-BOX unit tests that inject known mappings via the test-only
// helpers InjectMappingForTest / ClearMappingsForTest and call the internal
// ApplyStaleFunctionIdPatches method directly.  This is the tightest signal for
// the fix: it would have been RED (no replacement) before the fix existed.

using Xunit;
using AlRunnerV2;
using AlRunnerV2.Infrastructure;

namespace AlRunnerV2.Tests;

public sealed class StaleFunctionIdPatcherTests : IDisposable
{
    public StaleFunctionIdPatcherTests()
        => StaleSymbolUpgrader.ClearMappingsForTest();

    public void Dispose()
        => StaleSymbolUpgrader.ClearMappingsForTest();

    // ── Positive: empty registry ──────────────────────────────────────────────

    [Fact]
    public void EmptyRegistry_ReturnsCodeUnchanged()
    {
        const string code = "await this.Target.Invoke(-1033141710, new object[] { this.vendor });";
        var result = BcAssembler.ApplyStaleFunctionIdPatches(code);
        Assert.Equal(code, result);
    }

    // ── Positive: single stale ID ─────────────────────────────────────────────

    [Fact]
    public void SingleStaleId_IsReplaced()
    {
        StaleSymbolUpgrader.InjectMappingForTest(-1033141710, 266061949);
        const string code = "await this.Target.Invoke(-1033141710, new object[] { this.vendor });";
        var result = BcAssembler.ApplyStaleFunctionIdPatches(code);
        Assert.Contains("266061949", result);
        Assert.DoesNotContain("-1033141710", result);
    }

    // ── Positive: multiple stale IDs in one source file ───────────────────────

    [Fact]
    public void MultipleStaleIds_AllReplaced()
    {
        StaleSymbolUpgrader.InjectMappingForTest(-1033141710, 266061949);   // CreateVendor
        StaleSymbolUpgrader.InjectMappingForTest(-1573793025, 2105436939);  // CreatePurchaseInvoice
        StaleSymbolUpgrader.InjectMappingForTest(1658684108, 1053813575);   // CreatePurchaseCreditMemo

        const string code =
            "this.Target.Invoke(-1033141710, args1);\n" +
            "this.Target.Invoke(-1573793025, args2);\n" +
            "this.Target.Invoke(1658684108, args3);";

        var result = BcAssembler.ApplyStaleFunctionIdPatches(code);

        Assert.Contains("266061949", result);
        Assert.Contains("2105436939", result);
        Assert.Contains("1053813575", result);
        Assert.DoesNotContain("-1033141710", result);
        Assert.DoesNotContain("-1573793025", result);
        Assert.DoesNotContain("1658684108", result);
    }

    // ── Negative: stale ID that is a digit-prefix of another integer ──────────
    // The pattern uses (?<!\d) and (?!\d) to avoid matching e.g. 1234 inside 12345.

    [Fact]
    public void StaleId_IsNotReplacedWhenEmbeddedInsideLargerNumber()
    {
        StaleSymbolUpgrader.InjectMappingForTest(1234, 9999);
        // 12345 contains "1234" as a prefix — must NOT be replaced.
        const string code = "this.Target.Invoke(12345, args);";
        var result = BcAssembler.ApplyStaleFunctionIdPatches(code);
        Assert.Equal(code, result);
    }

    [Fact]
    public void StaleId_IsNotReplacedWhenUsedAsSuffix()
    {
        StaleSymbolUpgrader.InjectMappingForTest(1234, 9999);
        // 01234 — suffix match; must NOT be replaced.
        const string code = "this.Target.Invoke(01234, args);";
        var result = BcAssembler.ApplyStaleFunctionIdPatches(code);
        Assert.Equal(code, result);
    }

    // ── Positive: exact standalone match IS replaced ──────────────────────────

    [Fact]
    public void StaleId_IsReplacedWhenExactlyIsolated()
    {
        StaleSymbolUpgrader.InjectMappingForTest(1234, 9999);
        const string code = "this.Target.Invoke(1234, args);";
        var result = BcAssembler.ApplyStaleFunctionIdPatches(code);
        Assert.Contains("9999", result);
        Assert.DoesNotContain("1234", result);
    }

    // ── Positive: already-current ID is a no-op (same id in both slots) ───────

    [Fact]
    public void AlreadyCurrentId_NoMapping_IsUnchanged()
    {
        // 266061949 is the current BC 28.1 ID for CreateVendor.
        // No mapping registered → code left alone.
        const string code = "this.Target.Invoke(266061949, args);";
        var result = BcAssembler.ApplyStaleFunctionIdPatches(code);
        Assert.Equal(code, result);
    }
}
