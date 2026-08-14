using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Issue #1901 — a warm process that re-runs the SAME multi-bundle set more than once
/// (server mode's edit-and-rerun contract; <c>--watch</c> shares the identical
/// in-process reload mechanism) can serve a cross-app call the PREVIOUS cycle's code.
/// .NET cannot unload assemblies, so editing only a DEPENDENCY app between two
/// <c>runTests</c> requests in the same session leaves both the old and the new
/// generation of that app's assembly resident. The type finders that resolve an AL
/// object dynamically by id (<c>CodeunitPatches.FindCodeunitType</c> et al.) used to
/// prefer <c>CurrentTestAssembly</c> — which only ever covers the app CURRENTLY
/// executing — and otherwise scan every loaded assembly with no notion of "which
/// generation is current", so a call from the dependent app's test code into the
/// edited dependency could bind to whichever generation
/// <c>AppDomain.CurrentDomain.GetAssemblies()</c> happened to enumerate first: in
/// practice, the STALE one. The test kept reporting PASS against pre-edit behaviour —
/// silent, no crash, no diagnostic.
///
/// RED (pre-fix): request 2 below still reports PASS after the dependency's answer
/// changed from 42 to 43 — the stale generation served the call.
/// GREEN (post-fix): request 2 FAILS with a message naming the real (43) answer —
/// proof the freshly-edited generation actually ran.
///
/// A companion test below proves the sibling failure mode named in #1901's "Likely
/// fix" list: a missed supersede in the event-subscriber registry. It uses a
/// manually-declared codeunit-to-codeunit event (dispatched by
/// <c>CodeunitEventDispatcher</c> off <c>EventSubscriberPatches.GetCodeunitSubscribers</c>,
/// re-scanned fresh on every call) rather than a table trigger, so the proof is
/// isolated to the subscriber registry itself and does not also exercise the
/// SEPARATE, already-documented table-metadata-reload limitation in
/// <c>docs/server-mode.md</c> (BC's own skeleton <c>NCLMetadata.metadataCacheEntries</c>
/// is deliberately not cleared across a reload, so a table declared in a dependency
/// app can lag).
///
/// RED (pre-fix) here is "fires ZERO times", not "fires twice": a discovery scan can
/// run BEFORE the dependency app's fresh generation has (re)registered for THIS cycle
/// (<c>EventSubscriberPatches.EnsureRegistryFresh</c> is re-entered from several call
/// sites, and the very first one in a cycle can land ahead of the dependency's own
/// <c>SetTestAssembly</c>). <c>EventSubscriberPatches</c>' own publisher-type lookup
/// (<c>FindClrType</c>/<c>_codeunitTypeCache</c>) then permanently caches the STALE
/// generation resolved at that early moment, so the sentinel <c>γeventScope</c> field
/// gets seeded on the WRONG (previous-cycle) generation's <c>OnFire_Scope</c> class.
/// The freshly-edited generation Test's code actually calls into never gets its own
/// <c>γeventScope</c> seeded, so BC's own generated guard
/// (<c>if (γeventScope == null &amp;&amp; !recorder) return;</c>) skips dispatch
/// entirely — the subscriber never runs, silently. <c>DispatchCore</c> already
/// deduplicates by AL identity (codeunit id + method name), which is why this
/// specific dispatch path cannot manifest as a literal double-fire even when a stale
/// generation's [EventSubscriber] method is (redundantly, harmlessly) rediscovered
/// alongside the fresh one — the fix (superseding stale cache entries and pruning
/// stale discovery-registry entries) closes the under-fire failure mode, which is the
/// one this specific fixture can actually observe going wrong.
///
/// Spawns the real runner in --server mode; needs the BC artifact cache. Skips
/// (no-op) when absent.
/// </summary>
public class ServerCrossAppStaleGenerationTests
{
    private static (string libDir, string testDir, string answerFile) MakeLibTestPair(string rootName)
    {
        var root = Path.Combine(Path.GetTempPath(), rootName, Guid.NewGuid().ToString("N"));
        var libDir = Path.Combine(root, "Lib");
        var testDir = Path.Combine(root, "Test");
        Directory.CreateDirectory(libDir);
        Directory.CreateDirectory(testDir);

        const string libId = "d1a2b3c4-d5e6-4f70-8a91-b2c3d4e5f701";

        File.WriteAllText(Path.Combine(libDir, "app.json"), $$"""
        {
          "id": "{{libId}}",
          "name": "WS Lib",
          "publisher": "AL Runner Repro",
          "version": "1.0.0.0",
          "dependencies": [],
          "idRanges": [ { "from": 60960, "to": 60969 } ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "runtime": "14.0"
        }
        """);
        var answerFile = Path.Combine(libDir, "Answer.Codeunit.al");
        File.WriteAllText(answerFile, """
        codeunit 60960 "WS Answer"
        {
            procedure Answer(): Integer
            begin
                exit(42);
            end;
        }
        """);
        File.WriteAllText(Path.Combine(libDir, "FireEvent.Codeunit.al"), """
        codeunit 60963 "WS Fire Event"
        {
            [IntegrationEvent(false, false)]
            local procedure OnFire(var FireCount: Integer)
            begin
            end;

            procedure Fire(var FireCount: Integer)
            begin
                OnFire(FireCount);
            end;
        }

        codeunit 60964 "WS Fire Sub"
        {
            [EventSubscriber(ObjectType::Codeunit, Codeunit::"WS Fire Event", 'OnFire', '', false, false)]
            local procedure OnFireHandler(var FireCount: Integer)
            begin
                FireCount += 1;
            end;
        }
        """);

        File.WriteAllText(Path.Combine(testDir, "app.json"), $$"""
        {
          "id": "d1a2b3c4-d5e6-4f70-8a91-b2c3d4e5f702",
          "name": "WS Test",
          "publisher": "AL Runner Repro",
          "version": "1.0.0.0",
          "dependencies": [
            { "id": "{{libId}}", "name": "WS Lib", "publisher": "AL Runner Repro", "version": "1.0.0.0" }
          ],
          "idRanges": [ { "from": 60970, "to": 60979 } ],
          "platform": "1.0.0.0",
          "application": "1.0.0.0",
          "runtime": "14.0"
        }
        """);
        File.WriteAllText(Path.Combine(testDir, "WsTests.Codeunit.al"), """
        codeunit 60970 "WS Tests"
        {
            Subtype = Test;

            [Test]
            procedure LibAnswer_Is42()
            var
                Ans: Codeunit "WS Answer";
                Actual: Integer;
            begin
                Actual := Ans.Answer();
                if Actual <> 42 then
                    Error('Expected 42 but got %1', Actual);
            end;
        }

        codeunit 60971 "WS Sub Test"
        {
            Subtype = Test;

            [Test]
            procedure Fire_SubscriberFiresExactlyOnce()
            var
                FireEvt: Codeunit "WS Fire Event";
                Count: Integer;
            begin
                Count := 0;
                FireEvt.Fire(Count);
                if Count <> 1 then
                    Error('subscriber fired %1 time(s), expected exactly 1 — a stale generation of WS Fire Sub is still registered', Count);
            end;
        }
        """);

        return (libDir, testDir, answerFile);
    }

    private static string Req(string libDir, string testDir)
        => JsonSerializer.Serialize(new
        {
            command = "runTests",
            sourcePaths = new[] { libDir, testDir },
            packagePaths = Array.Empty<string>(),
        });

    [SkippableFact]
    public async Task RunTests_Then_EditDependencyOnly_Rerun_ObservesTheEditNotTheStaleGeneration()
    {
        TestArtifacts.SkipIfMissing();

        var (libDir, testDir, answerFile) = MakeLibTestPair("al-runner-xapp-stale-gen");
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-xapp-stale-gen-cache", Guid.NewGuid().ToString("N"));
        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        // ── Cycle 1: fresh generation of WS Lib, Answer() == 42 — must PASS ──────
        var lines1 = await server.SendRequestStreamingAsync(Req(libDir, testDir), TimeSpan.FromSeconds(180));
        var (events1, d1) = ProtocolV2Streaming.Split(lines1);
        Assert.Equal(0, d1.GetProperty("failed").GetInt32());
        Assert.Equal(0, d1.GetProperty("errors").GetInt32());
        var answerEvent1 = events1.Single(e => e.GetProperty("name").GetString()!.EndsWith("LibAnswer_Is42"));
        Assert.Equal("pass", answerEvent1.GetProperty("status").GetString());

        // ── Edit ONLY the dependency (WS Lib). The test codeunit is untouched and
        //    still asserts 42, so a correctly-superseded rerun MUST now FAIL. ─────
        var source = await File.ReadAllTextAsync(answerFile);
        var edited = source.Replace("exit(42);", "exit(43);");
        Assert.NotEqual(source, edited); // guard: the substitution actually applied
        await File.WriteAllTextAsync(answerFile, edited);

        // ── Cycle 2: SAME warm session, SAME request. The library now returns 43;
        //    a stale-generation resolution would still see 42 and report PASS. ────
        var lines2 = await server.SendRequestStreamingAsync(Req(libDir, testDir), TimeSpan.FromSeconds(180));
        var (events2, d2) = ProtocolV2Streaming.Split(lines2);
        var answerEvent2 = events2.Single(e => e.GetProperty("name").GetString()!.EndsWith("LibAnswer_Is42"));

        // The whole point of the RED->GREEN cycle: a PASS here means the runner served
        // the previous cycle's library code. Never silently accept that.
        Assert.Equal("fail", answerEvent2.GetProperty("status").GetString());
        var failMsg = answerEvent2.GetProperty("message").GetString() ?? "";
        // Proves the NEW generation actually ran (returned 43), not just "any failure".
        Assert.Contains("Expected 42 but got 43", failMsg);
        // The dependent app's OTHER test (unrelated to the edit) must still pass —
        // proof this is a targeted resolution fix, not a wholesale "everything after
        // an edit now fails" regression.
        Assert.Equal(1, d2.GetProperty("failed").GetInt32());
    }

    [SkippableFact]
    public async Task RunTests_Then_EditDependency_EventSubscriberSupersedes_FiresExactlyOnce()
    {
        TestArtifacts.SkipIfMissing();

        var (libDir, testDir, answerFile) = MakeLibTestPair("al-runner-xapp-stale-gen-sub");
        var cacheDir = Path.Combine(Path.GetTempPath(), "al-runner-xapp-stale-gen-sub-cache", Guid.NewGuid().ToString("N"));
        await using var server = await CliServer.StartAsync(new[] { "--cache", cacheDir });

        // ── Cycle 1: fresh generation of WS Lib — the subscriber must fire exactly
        //    once (sanity: the mechanism works at all). ───────────────────────────
        var lines1 = await server.SendRequestStreamingAsync(Req(libDir, testDir), TimeSpan.FromSeconds(180));
        var (events1, d1) = ProtocolV2Streaming.Split(lines1);
        var subEvent1 = events1.Single(e => e.GetProperty("name").GetString()!.EndsWith("Fire_SubscriberFiresExactlyOnce"));
        Assert.Equal("pass", subEvent1.GetProperty("status").GetString());

        // ── Edit the dependency (any textual change forces a fresh compile — a NEW
        //    "WS Fire Sub" generation, distinct from cycle 1's, both resident). ────
        var source = await File.ReadAllTextAsync(answerFile);
        var edited = source.Replace("exit(42);", "exit(42); // touched for cycle 2");
        Assert.NotEqual(source, edited);
        await File.WriteAllTextAsync(answerFile, edited);

        // ── Cycle 2: SAME warm session, SAME request, ONE Fire() call. A missed
        //    supersede in EventSubscriberPatches' own publisher-type cache seeds
        //    BC's γeventScope sentinel on the STALE generation's OnFire_Scope class
        //    instead of the fresh one Test's code actually calls into — so the fresh
        //    generation's dispatch guard never opens and the byref counter stays at
        //    0, not 1 (see the class doc comment for why this manifests as
        //    under-firing rather than double-firing for this dispatch path). ───────
        var lines2 = await server.SendRequestStreamingAsync(Req(libDir, testDir), TimeSpan.FromSeconds(180));
        var (events2, d2) = ProtocolV2Streaming.Split(lines2);
        var subEvent2 = events2.Single(e => e.GetProperty("name").GetString()!.EndsWith("Fire_SubscriberFiresExactlyOnce"));

        Assert.Equal("pass", subEvent2.GetProperty("status").GetString());
        Assert.Equal(0, d2.GetProperty("failed").GetInt32());
    }
}
