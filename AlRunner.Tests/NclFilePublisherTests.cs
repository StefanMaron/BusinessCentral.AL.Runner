// NclFilePublisherTests — #5018 and #5019.
//
// Both are Windows reports, and this suite runs on Linux, so the Windows-only behaviour is
// driven through NclFileOps: a recording fake stands in for a refused rename or a delete that
// Windows refuses because another process has the file loaded. The real-filesystem tests pin
// what Linux can observe directly. What neither can show is Windows itself; the PR body says so.

using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class NclFilePublisherTests
{
    private const string Ncl = "Microsoft.Dynamics.Nav.Ncl.dll";

    /// <summary>Records every mutating call; reads and existence come from a real directory
    /// so the equality check sees real bytes.</summary>
    private sealed class RecordingOps : NclFileOps
    {
        public readonly List<string> Calls = new();
        public Func<string, string, Exception?> MoveFault = (_, _) => null;
        public Func<string, Exception?> DeleteFault = _ => null;
        public Func<string, Exception?> ReadFault = _ => null;

        public override byte[] ReadAllBytesShared(string path)
        {
            if (ReadFault(path) is { } ex) throw ex;
            return base.ReadAllBytesShared(path);
        }
        public override void WriteAllBytes(string path, byte[] contents)
        {
            Calls.Add("write " + Path.GetFileName(path).Split(".tmp.")[0] + ".tmp");
            base.WriteAllBytes(path, contents);
        }
        public override void Move(string source, string dest)
        {
            Calls.Add("move " + Path.GetFileName(dest));
            if (MoveFault(source, dest) is { } ex) throw ex;
            base.Move(source, dest);
        }
        public override void Replace(string source, string dest)
        {
            Calls.Add("replace " + Path.GetFileName(dest));
            base.Replace(source, dest);
        }
        public override void Delete(string path)
        {
            Calls.Add("delete " + Path.GetFileName(path));
            if (DeleteFault(path) is { } ex) throw ex;
            base.Delete(path);
        }
        public override void Sleep(int milliseconds) => Calls.Add("sleep");
    }

    private static string NewDir(string name) =>
        Directory.CreateDirectory(TestScratch.Dir("ncl-publisher-" + name)).FullName;

    private static readonly byte[] Rewritten = { 0x4D, 0x5A, 1, 2, 3, 4, 5 };

    // ── #5019 / #5018 steady state: an identical destination is not written ──────────

    [Fact]
    public void PublishIfChanged_DestinationAlreadyHoldsTheBytes_WritesNothing()
    {
        var dir = NewDir("identical");
        var dest = Path.Combine(dir, Ncl);
        File.WriteAllBytes(dest, Rewritten);
        var ops = new RecordingOps();

        var outcome = NclFilePublisher.PublishIfChanged(dest, Rewritten, ops);

        Assert.Equal(NclFilePublisher.Outcome.AlreadyCurrent, outcome);
        Assert.Empty(ops.Calls);
        Assert.Empty(Directory.GetFiles(dir).Where(f => Path.GetFileName(f) != Ncl));
    }

    [Fact]
    public void PublishIfChanged_DestinationDiffers_WritesTheNewBytes()
    {
        var dir = NewDir("differs");
        var dest = Path.Combine(dir, Ncl);
        File.WriteAllBytes(dest, new byte[] { 0x4D, 0x5A, 9, 9 });
        var ops = new RecordingOps();

        var outcome = NclFilePublisher.PublishIfChanged(dest, Rewritten, ops);

        Assert.Equal(NclFilePublisher.Outcome.Written, outcome);
        Assert.Equal(Rewritten, File.ReadAllBytes(dest));
        Assert.Empty(Directory.GetFiles(dir).Where(f => Path.GetFileName(f) != Ncl));
    }

    [Fact]
    public void PublishIfChanged_DestinationMissing_CreatesIt()
    {
        var dir = NewDir("missing");
        var dest = Path.Combine(dir, Ncl);

        var outcome = NclFilePublisher.PublishIfChanged(dest, Rewritten, new RecordingOps());

        Assert.Equal(NclFilePublisher.Outcome.Written, outcome);
        Assert.Equal(Rewritten, File.ReadAllBytes(dest));
    }

    [Fact]
    public void PublishIfChanged_DestinationSameLengthOneByteDifferent_Writes()
    {
        var dir = NewDir("onebyte");
        var dest = Path.Combine(dir, Ncl);
        var near = (byte[])Rewritten.Clone();
        near[^1] ^= 0xFF;
        File.WriteAllBytes(dest, near);

        var outcome = NclFilePublisher.PublishIfChanged(dest, Rewritten, new RecordingOps());

        Assert.Equal(NclFilePublisher.Outcome.Written, outcome);
        Assert.Equal(Rewritten, File.ReadAllBytes(dest));
    }

    /// <summary>A symlink's content can change without our path being written, so equal bytes
    /// behind a link are not "already current": it is replaced by a real file.</summary>
    [Fact]
    public void PublishIfChanged_DestinationIsSymlinkToIdenticalBytes_ReplacesItWithARealFile()
    {
        var dir = NewDir("symlink");
        var target = Path.Combine(dir, "elsewhere.dll");
        File.WriteAllBytes(target, Rewritten);
        var dest = Path.Combine(dir, Ncl);
        try { File.CreateSymbolicLink(dest, target); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // no symlink privilege (Windows without Developer Mode): nothing to test
        }

        var outcome = NclFilePublisher.PublishIfChanged(dest, Rewritten, new RecordingOps());

        Assert.Equal(NclFilePublisher.Outcome.Written, outcome);
        Assert.Null(new FileInfo(dest).LinkTarget);
        Assert.Equal(Rewritten, File.ReadAllBytes(dest));
    }

    /// <summary>Third state: a destination that cannot be read is not assumed current.</summary>
    [Fact]
    public void PublishIfChanged_DestinationUnreadable_FallsBackToWriting()
    {
        var dir = NewDir("unreadable");
        var dest = Path.Combine(dir, Ncl);
        File.WriteAllBytes(dest, Rewritten);
        var ops = new RecordingOps { ReadFault = _ => new IOException("sharing violation") };

        var outcome = NclFilePublisher.PublishIfChanged(dest, Rewritten, ops);

        Assert.Equal(NclFilePublisher.Outcome.Written, outcome);
        Assert.Contains("move " + Ncl, ops.Calls);
    }

    // ── #5018: the rename that never drops the name comes before ReplaceFile ────────

    [Fact]
    public void AtomicReplace_ExistingDestination_RenamesFirstAndNeverCallsReplaceFile()
    {
        var dir = NewDir("order");
        var dest = Path.Combine(dir, Ncl);
        File.WriteAllBytes(dest, new byte[] { 0x4D, 0x5A, 9 });
        var ops = new RecordingOps();

        NclFilePublisher.AtomicReplace(dest, Rewritten, ops);

        Assert.Equal(new[] { "write " + Ncl + ".tmp", "move " + Ncl }, ops.Calls);
        Assert.Equal(Rewritten, File.ReadAllBytes(dest));
    }

    /// <summary>The #1650 Defender case is kept: when the rename is refused, ReplaceFile is
    /// still tried on the same attempt, before any backoff.</summary>
    [Fact]
    public void AtomicReplace_RenameRefused_FallsBackToReplaceFileWithoutSleeping()
    {
        var dir = NewDir("refused");
        var dest = Path.Combine(dir, Ncl);
        File.WriteAllBytes(dest, new byte[] { 0x4D, 0x5A, 9 });
        var ops = new RecordingOps { MoveFault = (_, _) => new UnauthorizedAccessException("scanner holds it") };

        NclFilePublisher.AtomicReplace(dest, Rewritten, ops);

        Assert.Equal(new[] { "write " + Ncl + ".tmp", "move " + Ncl, "replace " + Ncl }, ops.Calls);
        Assert.Equal(Rewritten, File.ReadAllBytes(dest));
    }

    [Fact]
    public void AtomicReplace_RenameRefusedOnce_NoDestination_RetriesTheRename()
    {
        var dir = NewDir("retry");
        var dest = Path.Combine(dir, Ncl);
        var refused = 0;
        var ops = new RecordingOps
        {
            MoveFault = (_, _) => refused++ == 0 ? new IOException("scanner holds it") : null,
        };

        NclFilePublisher.AtomicReplace(dest, Rewritten, ops);

        Assert.Equal(new[] { "write " + Ncl + ".tmp", "move " + Ncl, "sleep", "move " + Ncl }, ops.Calls);
        Assert.Equal(Rewritten, File.ReadAllBytes(dest));
    }

    // ── #5019: leftover ReplaceFile backups are reaped, except one still loaded ──────

    [Fact]
    public void ReapReplaceBackups_DeletesBackupsOfTheNamedFileOnly()
    {
        var dir = NewDir("reap");
        var keep = new[]
        {
            Ncl,
            Ncl + ".tmp.0123456789abcdef0123456789abcdef",
            "Microsoft.Dynamics.Nav.Types.dll~RF1a2b.TMP",
            Ncl + "~RFnothex.TMP",
            Ncl + "~RF1a2b.TMP.keep",
        };
        var reap = new[] { Ncl + "~RF1a2b.TMP", Ncl + "~RFC0FFEE.TMP", Ncl + "~rf9.tmp" };
        foreach (var n in keep.Concat(reap)) File.WriteAllBytes(Path.Combine(dir, n), Rewritten);

        var removed = NclFilePublisher.ReapReplaceBackups(dir, Ncl, NclFileOps.Real);

        Assert.Equal(reap.Length, removed);
        Assert.Equal(keep.OrderBy(n => n, StringComparer.Ordinal),
            Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    /// <summary>A backup another live process still has loaded is the file Windows refuses to
    /// delete — the same refusal that made ReplaceFile leave it. The reap leaves it and moves on.</summary>
    [Fact]
    public void ReapReplaceBackups_DeleteRefused_LeavesThatFileAndReapsTheRest()
    {
        var dir = NewDir("reap-held");
        var held = Ncl + "~RF11.TMP";
        var free = Ncl + "~RF22.TMP";
        File.WriteAllBytes(Path.Combine(dir, held), Rewritten);
        File.WriteAllBytes(Path.Combine(dir, free), Rewritten);
        var ops = new RecordingOps
        {
            DeleteFault = p => Path.GetFileName(p) == held
                ? new UnauthorizedAccessException("in use by another process")
                : null,
        };

        var removed = NclFilePublisher.ReapReplaceBackups(dir, Ncl, ops);

        Assert.Equal(1, removed);
        Assert.True(File.Exists(Path.Combine(dir, held)));
        Assert.False(File.Exists(Path.Combine(dir, free)));
    }

    [Fact]
    public void ReapReplaceBackups_MissingDirectory_ReturnsZero()
    {
        var dir = Path.Combine(NewDir("reap-none"), "absent");
        Assert.Equal(0, NclFilePublisher.ReapReplaceBackups(dir, Ncl, NclFileOps.Real));
    }
}
