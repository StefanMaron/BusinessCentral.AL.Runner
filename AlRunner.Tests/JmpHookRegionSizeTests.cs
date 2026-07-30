using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Regression guard for the flaky Pageworks SEGV_ACCERR crash: JmpHook.InstallIndirect's mprotect
/// region size used to add an unconditional <c>+ pageSize</c>, extending the re-protected region a
/// full page PAST the patched cell. In .NET 8's interleaved code heap that adjacent page is often
/// live JIT code, and the data-page branch restored it to RW (no EXEC) — stripping the execute bit
/// off running code, which later faulted with SEGV_ACCERR (~60-80% flaky, JIT-layout dependent).
///
/// The behavioural proof is the Pageworks repro (crash rate 8/10 -> 0/10), which isn't CI-runnable.
/// These tests pin the pure page-rounding math instead — the exact invariant the old code broke.
/// </summary>
public class JmpHookRegionSizeTests
{
    private const long PageSize = 4096;

    [Theory]
    [InlineData(0)]      // cell at page start
    [InlineData(100)]    // cell mid-page
    [InlineData(4088)]   // cell ending exactly at the page boundary (4088 + 8 == 4096)
    public void WithinSinglePage_CoversExactlyOnePage_NotTwo(long offset)
    {
        long pageStart = 0x7f0000000000;
        long addr = pageStart + offset;

        var region = JmpHook.PageRoundedRegionSize(addr, pageStart, 8, PageSize);

        // The whole 8-byte cell fits in one page, so the region must be exactly one page.
        // The pre-fix formula ((addr-pageStart)+8+pageSize) returned >= two pages here — that
        // extra page is what got its EXEC bit stripped. This assertion fails on the old code.
        Assert.Equal((nuint)PageSize, region);
    }

    [Fact]
    public void CrossingAPageBoundary_CoversExactlyTwoPages()
    {
        long pageStart = 0x7f0000000000;
        long addr = pageStart + 4090; // 4090 + 8 == 4098 -> spills 2 bytes into the next page

        var region = JmpHook.PageRoundedRegionSize(addr, pageStart, 8, PageSize);

        Assert.Equal((nuint)(2 * PageSize), region);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4087)]
    [InlineData(4088)]
    [InlineData(4090)]
    [InlineData(4095)]
    public void Invariants_CoversTheCell_IsPageMultiple_NeverAFullExtraPage(long offset)
    {
        long pageStart = 0x7f0000000000;
        long addr = pageStart + offset;
        const int bytes = 8;

        long region = (long)JmpHook.PageRoundedRegionSize(addr, pageStart, bytes, PageSize);

        // 1. Covers the whole write.
        Assert.True(pageStart + region >= addr + bytes,
            $"region must cover the {bytes}-byte write at offset {offset}");
        // 2. Is a whole number of pages (mprotect requires page-aligned length here).
        Assert.Equal(0, region % PageSize);
        // 3. Never extends a full page beyond the cell's end — the bug that stripped EXEC off a
        //    neighbouring code page. Upper bound = end-of-write rounded up to the next page.
        long minCovering = ((offset + bytes + PageSize - 1) / PageSize) * PageSize;
        Assert.Equal(minCovering, region);
    }
}
