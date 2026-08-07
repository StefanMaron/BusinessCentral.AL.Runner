// PkgDedupStagingPublishTests — RED→GREEN guard for #1691.
//
// BcCompiler's package-dedup staging used to end in a bare
//
//     try { Directory.Move(tmp, stage); }
//     catch { if (!Directory.Exists(stage)) throw; ... }
//
// On Windows that move intermittently fails with "Access to the path '...tmp-...' is
// denied" when the staged files were written moments earlier (AV/indexer handle), so the
// rethrow killed the run with `EMIT-FAIL — IOException` — roughly 1 run in 5 on a
// non-admin box, where every staged entry is a full copy rather than a symlink.
//
// The failing move cannot be provoked portably by holding a handle, so these drive the
// same code path with a blocker that makes Directory.Move throw while leaving `stage`
// non-existent as a DIRECTORY: a plain file sitting at the stage path. That is exactly the
// state the old rethrow branch keyed on (`!Directory.Exists(stage)` → throw), so it
// exercises the real fallback, and the injected backoff hook drives the retry
// deterministically instead of relying on timing.
using Xunit;

namespace AlRunner.Tests;

public class PkgDedupStagingPublishTests : IDisposable
{
    private readonly string _root;

    public PkgDedupStagingPublishTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "al-runner-pkgdedup-tests",
                             Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // A scratch dir shaped like the real one: one staged .app per picked package.
    private string StagedTmp(params string[] appNames)
    {
        var tmp = Path.Combine(_root, "key.tmp-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tmp);
        foreach (var name in appNames)
            File.WriteAllText(Path.Combine(tmp, name), "staged:" + name);
        return tmp;
    }

    private string StagePath => Path.Combine(_root, "key");

    [Fact]
    public void Publish_MoveSucceeds_ReturnsStageWithContentAndRemovesTmp()
    {
        var tmp = StagedTmp("A.app", "B.app");

        var used = AlRunner.Infrastructure.PkgDedupStaging.Publish(tmp, StagePath);

        Assert.Equal(StagePath, used);
        Assert.False(Directory.Exists(tmp), "scratch dir should be gone after a clean move");
        Assert.Equal(new[] { "A.app", "B.app" }, Directory.GetFiles(used)
            .Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal("staged:A.app", File.ReadAllText(Path.Combine(used, "A.app")));
    }

    [Fact]
    public void Publish_StageAlreadyPublished_AdoptsItAndDropsTmp()
    {
        // A concurrent compile won the race for the same content-addressed key.
        Directory.CreateDirectory(StagePath);
        File.WriteAllText(Path.Combine(StagePath, "A.app"), "published-by-the-racer");
        var tmp = StagedTmp("A.app");

        var used = AlRunner.Infrastructure.PkgDedupStaging.Publish(tmp, StagePath);

        Assert.Equal(StagePath, used);
        Assert.False(Directory.Exists(tmp), "the duplicate scratch dir should be cleaned up");
        // The winner's directory is left exactly as it was — not merged, not overwritten.
        Assert.Equal("published-by-the-racer", File.ReadAllText(Path.Combine(used, "A.app")));
    }

    [Fact]
    public void Publish_TransientFailure_RetriesAndStillPublishesStage()
    {
        var blocker = StagePath;
        File.WriteAllText(blocker, "not a directory"); // makes Directory.Move throw
        var tmp = StagedTmp("A.app");

        var attemptsSeen = new List<int>();
        var used = AlRunner.Infrastructure.PkgDedupStaging.Publish(
            tmp, StagePath, warn: null, attempts: 5,
            // Clear the blocker after the first failure — the transient-handle case.
            backoff: attempt => { attemptsSeen.Add(attempt); if (attempt == 1) File.Delete(blocker); });

        // Retried exactly once, then succeeded: the shared reusable dir IS published, which
        // is the whole point of retrying rather than falling straight back to the scratch dir.
        Assert.Equal(new[] { 1 }, attemptsSeen.ToArray());
        Assert.Equal(StagePath, used);
        Assert.False(Directory.Exists(tmp));
        Assert.Equal("staged:A.app", File.ReadAllText(Path.Combine(used, "A.app")));
    }

    // Negative direction: the move never succeeds. The old code threw here (killing a run
    // whose tests had not even started); the contract now is a usable directory + a notice.
    [Fact]
    public void Publish_PersistentFailure_FallsBackToTmpAndWarnsInsteadOfThrowing()
    {
        File.WriteAllText(StagePath, "not a directory"); // never cleared
        var tmp = StagedTmp("A.app", "B.app");
        var warn = new StringWriter();

        var used = AlRunner.Infrastructure.PkgDedupStaging.Publish(
            tmp, StagePath, warn, attempts: 3, backoff: _ => { });

        // Falls back to the scratch dir — which carries the identical staged set, so the
        // compile that consumes it sees exactly what the published dir would have held.
        Assert.Equal(tmp, used);
        Assert.True(Directory.Exists(tmp), "the fallback directory must survive");
        Assert.Equal(new[] { "A.app", "B.app" }, Directory.GetFiles(used)
            .Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // And it says so, naming both paths — a silent fallback would hide a real cache miss.
        var text = warn.ToString();
        Assert.Contains("[pkgdedup]", text);
        Assert.Contains(StagePath, text);
        Assert.Contains(tmp, text);
        Assert.Contains("3 attempt(s)", text);
    }

    [Fact]
    public void Publish_PersistentFailure_ExhaustsEveryAttemptBeforeGivingUp()
    {
        File.WriteAllText(StagePath, "not a directory");
        var tmp = StagedTmp("A.app");

        var attemptsSeen = new List<int>();
        var used = AlRunner.Infrastructure.PkgDedupStaging.Publish(
            tmp, StagePath, warn: null, attempts: 4,
            backoff: attempt => attemptsSeen.Add(attempt));

        // 4 attempts → backoff after 1,2,3 and none after the last: no wasted final sleep.
        Assert.Equal(new[] { 1, 2, 3 }, attemptsSeen.ToArray());
        Assert.Equal(tmp, used);
    }
}
