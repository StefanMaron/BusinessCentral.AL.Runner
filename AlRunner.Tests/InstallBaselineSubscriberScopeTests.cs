using System.Diagnostics;
using System.Text;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #5068: the dependency+company install baseline is captured after the dependency Install
/// triggers ran with every automatic event subscriber in the process armed, so those
/// subscribers' rows are part of it. Its key has to say which subscribers those were, or a
/// later process with a different set restores rows its own subscribers never wrote.
///
/// The fixture's dependency raises an integration event from its OnInstallAppPerCompany
/// trigger; an app's automatic subscriber inserts a row named SUB. Every process runs ONE
/// bundle, so the in-process path cannot answer — only the on-disk tier, shared through the
/// default cache root, carries a baseline from one process to the next. Each bundle's own AL
/// test reads SUB back, so a wrongly shared baseline fails by value, not only by marker.
///
/// Spawns the real runner; needs the BC artifact cache. Skips (no-op) when absent.
/// </summary>
public class InstallBaselineSubscriberScopeTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    private static readonly string ProjectPath = Path.Combine(RepoRoot, "AlRunner");

    private const string Miss = "InstallBaseline.DepCompanyCache MISS";
    private const string DiskHit = "InstallBaseline.DepCompanyCache DISK-HIT";

    /// <summary>Warm with the same subscriber: a disk HIT. Warm after that subscriber's body
    /// changed: a MISS, and the bundle sees the row its CURRENT subscriber writes.</summary>
    [SkippableFact]
    public void ChangedSubscriber_MissesTheBaseline_UnchangedSubscriber_HitsIt()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-ib-subscriber-change");
        try
        {
            var fx = Fixture.Write(root, 63300);
            var a = fx.WriteBundle("main-a", "A", 63305, Fixture.Subscriber.Automatic, writes: "SUB-A1", expect: "SUB-A1");

            var cold = Run(a);
            AssertPassed(cold);
            Assert.Equal(1, Count(cold.output, Miss));
            Assert.Equal(0, Count(cold.output, DiskHit));

            var warm = Run(a);
            AssertPassed(warm);
            Assert.Equal(0, Count(warm.output, Miss));
            Assert.Equal(1, Count(warm.output, DiskHit));

            // The same app, its subscriber now writing a different value: new MVID, new key.
            fx.WriteBundle("main-a", "A", 63305, Fixture.Subscriber.Automatic, writes: "SUB-A2", expect: "SUB-A2");
            var changed = Run(a);
            AssertPassed(changed);
            Assert.Equal(1, Count(changed.output, Miss));
            Assert.Equal(0, Count(changed.output, DiskHit));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>An app with no automatic subscriber does not restore the baseline an app with
    /// one wrote — and does share one with an app whose only subscriber is manual-binding, so a
    /// test assembly's own identity is not what the key carries.</summary>
    [SkippableFact]
    public void AppWithoutTheSubscriber_DoesNotRestoreItsRows_ManualSubscriberSharesTheBaseline()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("al-runner-ib-subscriber-scope");
        try
        {
            var fx = Fixture.Write(root, 63400);
            var a = fx.WriteBundle("main-a", "A", 63405, Fixture.Subscriber.Automatic, writes: "SUB-A", expect: "SUB-A");
            var b = fx.WriteBundle("main-b", "B", 63410, Fixture.Subscriber.None, writes: null, expect: null);
            var c = fx.WriteBundle("main-c", "C", 63415, Fixture.Subscriber.Manual, writes: "SUB-C", expect: null);

            var withSub = Run(a);
            AssertPassed(withSub);
            Assert.Equal(1, Count(withSub.output, Miss));

            var without = Run(b);
            AssertPassed(without);
            Assert.Equal(1, Count(without.output, Miss));
            Assert.Equal(0, Count(without.output, DiskHit));

            var manual = Run(c);
            AssertPassed(manual);
            Assert.Equal(0, Count(manual.output, Miss));
            Assert.Equal(1, Count(manual.output, DiskHit));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private static void AssertPassed((string output, int exit) run)
    {
        Assert.True(run.exit == 0 && Count(run.output, "1P/0F/0E") == 1,
            $"expected the bundle's one test to pass, exit {run.exit}:\n{run.output}");
    }

    private static (string output, int exit) Run(string bundle)
    {
        var args = new StringBuilder(TestBuildConfig.RunArgs(ProjectPath));
        args.Append(TestBuildConfig.BcVersionArg);
        args.Append(" --package-cache \"").Append(TestArtifacts.PlatformAppsDir()).Append('"');
        args.Append(" \"").Append(bundle).Append('"');
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet", Arguments = args.ToString(),
            RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = RepoRoot,
            Environment = { ["AL_RUNNER_PERF"] = "1" },
        };
        var sb = new StringBuilder();
        var p = Process.Start(psi)!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(300_000)) { try { p.Kill(true); } catch { } throw new TimeoutException("runner hung"); }
        p.WaitForExit();
        lock (sb) return (sb.ToString(), p.ExitCode);
    }

    private static int Count(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += needle.Length;
        }
        return count;
    }

    /// <summary>A seed dependency whose install trigger inserts SEED-1 and then raises
    /// OnAfterSeed, and the bundles depending on it. The seed's id is a fresh GUID, so its
    /// assembly, and every key built on it, is unique to the test invocation.</summary>
    private sealed class Fixture
    {
        internal enum Subscriber { None, Automatic, Manual }

        private readonly string _parent;
        private readonly string _seedId;
        private readonly Dictionary<string, string> _appIds = new();
        private const string SeedName = "Seed Sub Scope";

        private Fixture(string parent, string seedId) { _parent = parent; _seedId = seedId; }

        internal static Fixture Write(string root, int baseId)
        {
            var seedDir = Path.Combine(root, "seed");
            Directory.CreateDirectory(seedDir);
            var seedId = Guid.NewGuid().ToString();
            WriteAppJson(seedDir, seedId, SeedName, "[]", baseId);
            File.WriteAllText(Path.Combine(seedDir, "Seed.al"), $$"""
            table {{baseId}} "Sub Scope Seed"
            {
                DataClassification = SystemMetadata;
                fields { field(1; "Code"; Code[20]) { } field(2; "Description"; Text[50]) { } }
                keys { key(PK; "Code") { Clustered = true; } }
            }

            codeunit {{baseId + 1}} "Sub Scope Publisher"
            {
                [IntegrationEvent(false, false)]
                procedure OnAfterSeed()
                begin
                end;
            }

            codeunit {{baseId + 2}} "Sub Scope Install"
            {
                Subtype = Install;

                trigger OnInstallAppPerCompany()
                var
                    SeedRow: Record "Sub Scope Seed";
                    Publisher: Codeunit "Sub Scope Publisher";
                begin
                    SeedRow.Init();
                    SeedRow.Code := 'SEED-1';
                    SeedRow.Description := 'seed';
                    SeedRow.Insert(true);
                    Publisher.OnAfterSeed();
                end;
            }
            """);
            return new Fixture(root, seedId);
        }

        /// <summary>Writes (or rewrites) a bundle. <paramref name="expect"/> null asserts SUB is
        /// absent; otherwise SUB must be present with that Description.</summary>
        internal string WriteBundle(string dirName, string tag, int baseId, Subscriber subscriber,
            string? writes, string? expect)
        {
            var dir = Path.Combine(_parent, dirName);
            Directory.CreateDirectory(dir);
            var dep = $$"""[ { "id": "{{_seedId}}", "name": "{{SeedName}}", "publisher": "AL Runner Install Seed", "version": "1.0.0.0" } ]""";
            // A rewrite keeps the app's id: what changes between runs is the subscriber alone.
            if (!_appIds.TryGetValue(dirName, out var appId))
                _appIds[dirName] = appId = Guid.NewGuid().ToString();
            WriteAppJson(dir, appId, "Sub Scope " + tag, dep, baseId);

            var sub = subscriber == Subscriber.None ? "" : $$"""
            codeunit {{baseId + 1}} "Sub Scope {{tag}} Subscriber"
            {
                {{(subscriber == Subscriber.Manual ? "EventSubscriberInstance = Manual;" : "")}}

                [EventSubscriber(ObjectType::Codeunit, Codeunit::"Sub Scope Publisher", 'OnAfterSeed', '', false, false)]
                local procedure HandleOnAfterSeed()
                var
                    SeedRow: Record "Sub Scope Seed";
                begin
                    SeedRow.Init();
                    SeedRow.Code := 'SUB';
                    SeedRow.Description := '{{writes}}';
                    SeedRow.Insert(true);
                end;
            }

            """;
            var check = expect == null
                ? "if SeedRow.Get('SUB') then Error('SUB is present, written by a subscriber this app does not have: %1', SeedRow.Description);"
                : $"if not SeedRow.Get('SUB') then Error('SUB is missing'); if SeedRow.Description <> '{expect}' then Error('SUB Description was %1', SeedRow.Description);";
            File.WriteAllText(Path.Combine(dir, "Main.al"), sub + $$"""
            codeunit {{baseId}} "Sub Scope {{tag}} Test"
            {
                Subtype = Test;

                [Test]
                procedure InstallBaselineMatchesThisAppsSubscribers()
                var
                    SeedRow: Record "Sub Scope Seed";
                begin
                    if not SeedRow.Get('SEED-1') then Error('SEED-1 is missing');
                    {{check}}
                end;
            }
            """);
            return dir;
        }

        private static void WriteAppJson(string dir, string id, string name, string deps, int baseId)
            => File.WriteAllText(Path.Combine(dir, "app.json"), $$"""
            {
              "id": "{{id}}",
              "name": "{{name}}",
              "publisher": "AL Runner Install Seed",
              "version": "1.0.0.0",
              "dependencies": {{deps}},
              "platform": "1.0.0.0",
              "idRanges": [ { "from": {{baseId}}, "to": {{baseId + 4}} } ],
              "runtime": "14.0"
            }
            """);
    }
}
