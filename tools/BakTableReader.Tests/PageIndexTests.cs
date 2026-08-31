using BakTableReader;
using Xunit;

namespace BakTableReader.Tests;

public class PageIndexTests
{
    private static byte[] MakePageBytes(byte headerVersion, byte type, int selfPageId, short selfFileId)
    {
        var page = new byte[PageHeader.PageLength];
        page[0] = headerVersion;
        page[1] = type;
        BitConverter.GetBytes(selfPageId).CopyTo(page, 32);
        BitConverter.GetBytes(selfFileId).CopyTo(page, 36);
        return page;
    }

    [Fact]
    public void Build_ResolvesAddressByStoredHeaderNotByBlockPosition()
    {
        // Reproduces the discontinuity AL Runner #2241 found in a real BC demo
        // .bak: after some point in the file, "absolute block == logical
        // PageId + constant" stops holding (an MTF structure is spliced into
        // the otherwise page-aligned stream). Block 0 claims to be logical
        // page 9 (matching a "+9" offset); block 1 claims to be logical page
        // 776 (NOT "+9" -- if PageIndex fell back to position arithmetic
        // instead of trusting each block's own header, resolving page 776
        // would silently return the wrong block).
        using var stream = new MemoryStream();
        stream.Write(MakePageBytes(1, 1, selfPageId: 9, selfFileId: 1));
        stream.Write(MakePageBytes(1, 1, selfPageId: 776, selfFileId: 1));
        stream.Position = 0;

        var index = PageIndex.Build(stream);

        Assert.Equal(2, index.PageCount);
        Assert.Equal(0, index.GetBlock(new PagePointer(1, 9)));
        Assert.Equal(1, index.GetBlock(new PagePointer(1, 776)));
    }

    [Fact]
    public void Build_SkipsBlocksWithoutAValidHeader()
    {
        using var stream = new MemoryStream();
        stream.Write(MakePageBytes(1, 1, selfPageId: 1, selfFileId: 1));
        stream.Write(new byte[PageHeader.PageLength]); // header version 0 -- not a page
        stream.Write(MakePageBytes(1, 1, selfPageId: 2, selfFileId: 1));
        stream.Position = 0;

        var index = PageIndex.Build(stream);

        Assert.Equal(2, index.PageCount);
        Assert.Equal(0, index.GetBlock(new PagePointer(1, 1)));
        Assert.Equal(2, index.GetBlock(new PagePointer(1, 2)));
    }

    [Fact]
    public void Build_ToleratesATrailingPartialBlock()
    {
        // The real .bak trails with a 4096-byte remainder (half a page) --
        // presumably an MTF end-of-set marker. A short final read must not
        // throw; it is simply not indexed.
        using var stream = new MemoryStream();
        stream.Write(MakePageBytes(1, 1, selfPageId: 1, selfFileId: 1));
        stream.Write(new byte[PageHeader.PageLength / 2]);
        stream.Position = 0;

        var index = PageIndex.Build(stream);

        Assert.Equal(1, index.PageCount);
    }

    [Fact]
    public void GetBlock_ThrowsForAnUnindexedAddress()
    {
        using var stream = new MemoryStream();
        stream.Write(MakePageBytes(1, 1, selfPageId: 1, selfFileId: 1));
        stream.Position = 0;
        var index = PageIndex.Build(stream);

        Assert.Throws<KeyNotFoundException>(() => index.GetBlock(new PagePointer(1, 999)));
    }
}
