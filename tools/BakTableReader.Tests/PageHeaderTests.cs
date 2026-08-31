using BakTableReader;
using Xunit;

namespace BakTableReader.Tests;

public class PageHeaderTests
{
    private static byte[] BuildPage(byte headerVersion, byte type, short slotCount, int selfPageId,
        short selfFileId, int nextPageId, short nextFileId, short freeData)
    {
        var page = new byte[PageHeader.PageLength];
        page[0] = headerVersion;
        page[1] = type;
        BitConverter.GetBytes(nextPageId).CopyTo(page, 16);
        BitConverter.GetBytes(nextFileId).CopyTo(page, 20);
        BitConverter.GetBytes(slotCount).CopyTo(page, 22);
        BitConverter.GetBytes(freeData).CopyTo(page, 30);
        BitConverter.GetBytes(selfPageId).CopyTo(page, 32);
        BitConverter.GetBytes(selfFileId).CopyTo(page, 36);
        return page;
    }

    [Fact]
    public void TryParse_DecodesKnownFieldOffsets()
    {
        var page = BuildPage(headerVersion: 1, type: 1, slotCount: 3,
            selfPageId: 143, selfFileId: 1, nextPageId: 189, nextFileId: 1, freeData: 500);

        Assert.True(PageHeader.TryParse(page, out var header));
        Assert.Equal(PageType.Data, header.Type);
        Assert.Equal(3, header.SlotCount);
        Assert.Equal(new PagePointer(1, 143), header.Self);
        Assert.Equal(new PagePointer(1, 189), header.Next);
        Assert.Equal(500, header.FreeData);
    }

    [Fact]
    public void TryParse_RejectsWrongHeaderVersion()
    {
        var page = BuildPage(headerVersion: 2, type: 1, slotCount: 1,
            selfPageId: 1, selfFileId: 1, nextPageId: 0, nextFileId: 0, freeData: 0);

        Assert.False(PageHeader.TryParse(page, out _));
    }

    [Fact]
    public void TryParse_RejectsUndefinedPageType()
    {
        // 6 and 12 are not defined PageType values -- a real BC .bak has never
        // been observed to produce them, and a random byte in a LOB page body
        // must not be mistaken for a page header.
        var page = BuildPage(headerVersion: 1, type: 6, slotCount: 1,
            selfPageId: 1, selfFileId: 1, nextPageId: 0, nextFileId: 0, freeData: 0);

        Assert.False(PageHeader.TryParse(page, out _));
    }

    [Fact]
    public void ReadSlotArray_ReadsBackToFrontFromPageTail()
    {
        var page = new byte[PageHeader.PageLength];
        short[] expected = { 96, 250, 4038 };
        for (int i = 0; i < expected.Length; i++)
            BitConverter.GetBytes(expected[i]).CopyTo(page, page.Length - i * 2 - 2);

        var slots = PageHeader.ReadSlotArray(page, (short)expected.Length);

        Assert.Equal(expected, slots);
    }
}
