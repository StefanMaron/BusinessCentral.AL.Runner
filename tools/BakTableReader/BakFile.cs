namespace BakTableReader;

/// <summary>Read-only access to a BC demo .bak's SQL Server pages, addressed by
/// logical (FileId, PageId) rather than raw byte offset. See PageIndex for why
/// that indirection is required, not just a defensive nicety.</summary>
public sealed class BakFile : IDisposable
{
    private readonly FileStream _stream;
    public PageIndex Index { get; }

    private BakFile(FileStream stream, PageIndex index)
    {
        _stream = stream;
        Index = index;
    }

    public static BakFile Open(string path)
    {
        var stream = File.OpenRead(path);
        try
        {
            var index = PageIndex.Build(stream);
            return new BakFile(stream, index);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    public byte[] ReadPage(PagePointer address)
    {
        long block = Index.GetBlock(address);
        var buffer = new byte[PageHeader.PageLength];
        _stream.Seek(block * PageHeader.PageLength, SeekOrigin.Begin);
        int total = 0;
        while (total < buffer.Length)
        {
            int n = _stream.Read(buffer, total, buffer.Length - total);
            if (n == 0)
                throw new EndOfStreamException($"short read for page {address} at block {block}");
            total += n;
        }
        return buffer;
    }

    public void Dispose() => _stream.Dispose();
}
