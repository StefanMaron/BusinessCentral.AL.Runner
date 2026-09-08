// MetadataEquivalenceHarnessTests — the gate for issue #3533.
//
// What it asserts, in the order the issue asks for it:
//   1. every app tests/expectations/metadata-equivalence/apps.json declares is actually
//      covered, so the harness cannot quietly measure less than it claims;
//   2. every difference between BC's emitter and the runner's derivation is declared in the
//      allowlist, with a reason — anything else fails;
//   3. no allowlist entry has gone stale, so a landed reader fix must shrink the file;
//   4. the CURRENT reader reproduces four independently-predicted defect counts. That is the
//      test that shows this harness can fail: a harness green against a reader with 5,241
//      wrong Editable values would be proving nothing.
//
// (4) is deliberately expressed as exact values, not "greater than zero". It fails if the
// differ stops reporting, if it starts reporting more, and if the reader changes — which is
// the point: when #3545 lands, this test is the thing that must be updated, in the same
// commit, with the new measurement.
//
// Set AL_RUNNER_METADATA_EQUIVALENCE_REPORT=<path> to dump the full per-member diff; that
// dump is the working specification for the reader fix.

using AlRunner.Metadata;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AlRunner.Tests;

[Collection(BcEngineCollection.Name)]
public sealed class MetadataEquivalenceHarnessTests
{
    private readonly BcEngineFixture _engine;

    public MetadataEquivalenceHarnessTests(BcEngineFixture engine) => _engine = engine;

    private sealed record DeclaredApp(string Publisher, string Name, string Reason);

    private static IReadOnlyList<DeclaredApp> DeclaredApps()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(MetadataEquivalencePaths.AppsFile()));
        return doc.RootElement.GetProperty("apps").EnumerateArray()
            .Select(a => new DeclaredApp(
                a.GetProperty("publisher").GetString()!,
                a.GetProperty("name").GetString()!,
                a.GetProperty("reason").GetString()!))
            .ToArray();
    }

    /// <summary>
    /// Every bundle on this box, compared. Skips locally when nothing has been generated;
    /// FAILS on CI, where the generator runs by construction — a leg where every bundle is
    /// absent would otherwise report green having measured nothing, which is the exact defect
    /// this harness exists to remove (same reasoning as TestArtifacts.SkipIfMissingIn).
    /// </summary>
    private IReadOnlyList<MetadataEquivalenceReport> RunAll()
    {
        Skip.IfNot(_engine.Ready, _engine.SkipReason);

        var root = MetadataEquivalencePaths.GroundTruthDirForThisBuild();
        var bundles = MetadataEquivalenceHarness.LoadBundles(root);
        if (bundles.Count == 0)
        {
            // Names the exact --artifacts to pass. A dev box holds several BC builds and the
            // generator's own default is the NEWEST one, while this process loaded whichever
            // build the runner was compiled against — so "just run the generator" is not
            // actionable on its own and has already cost one round trip.
            var reason =
                $"no metadata ground-truth bundle under '{root}'. This test process loaded BC " +
                $"from '{AlRunner.Infrastructure.BcArtifacts.ServiceTierDir}', so generate for " +
                $"that build:{Environment.NewLine}" +
                $"  tools/gen-metadata-ground-truth.sh --artifacts " +
                $"\"{AlRunner.Infrastructure.BcArtifacts.ServiceTierDir}\"{Environment.NewLine}" +
                "Business Foundation is ~3s and System Application ~14s. " +
                "AL_RUNNER_METADATA_GROUND_TRUTH overrides where bundles are read from.";
            if (TestArtifacts.RunningOnCi)
                Assert.Fail(
                    "On a CI leg the ground truth is generated before `dotnet test` (see the " +
                    "'Generate BC metadata ground truth' step in .github/workflows/bc-tests.yml), " +
                    "so its absence is a workflow regression, not a legitimate skip. Skipping here " +
                    "would leave the metadata-equivalence gate reporting green while measuring " +
                    "nothing. " + reason);
            throw new SkipException(reason);
        }

        var reports = new List<MetadataEquivalenceReport>();
        foreach (var bundle in bundles)
        {
            var app = MetadataEquivalenceHarness.FindAppPackage(bundle);
            Assert.True(app is not null,
                $"ground truth exists for {bundle.Label} but its .app is not on this box. The " +
                "runner derives its side from that package's SymbolReference.json, so comparing " +
                "against a different build would compare two things never meant to agree. " +
                "Regenerate the bundle from the artifacts now present.");
            reports.Add(MetadataEquivalenceHarness.Compare(bundle, app!));
        }

        WriteReportIfAsked(reports);
        return reports;
    }

    [SkippableFact]
    public void Every_declared_app_is_actually_covered()
    {
        var reports = RunAll();

        foreach (var declared in DeclaredApps())
        {
            var hit = reports.FirstOrDefault(r =>
                r.Bundle.AppName == declared.Name && r.Bundle.AppPublisher == declared.Publisher);
            Assert.True(hit is not null,
                $"apps.json declares '{declared.Publisher}_{declared.Name}' must be covered " +
                $"({declared.Reason}), and no ground-truth bundle for it was found. Covered: " +
                string.Join(", ", reports.Select(r => r.Bundle.Label)));
            Assert.True(hit!.ObjectsCompared > 0,
                $"{hit.Bundle.Label}: a bundle exists but NOTHING was compared. " + hit.Summary);
        }
    }

    [SkippableFact]
    public void Every_object_in_a_compared_kind_really_was_compared()
    {
        // The bundle's own census is the denominator, so a table the runner cannot build is a
        // failure rather than a quietly smaller comparison.
        foreach (var report in RunAll())
        {
            Assert.True(report.Unbuildable.Count == 0,
                $"{report.Bundle.Label}: the runner produced no comparable metadata for " +
                $"{report.Unbuildable.Count} object(s):{Environment.NewLine}" +
                string.Join(Environment.NewLine, report.Unbuildable.Take(20)));

            foreach (var kind in report.KindsCompared)
                Assert.Equal(report.Bundle.Census[kind],
                    report.Bundle.Objects.Count(o => o.Kind == kind));
        }
    }

    [SkippableFact]
    public void Every_difference_is_declared_with_a_reason()
    {
        var allowlist = MetadataDifferenceAllowlist.Load(MetadataEquivalencePaths.AllowlistFile());
        var differences = RunAll().SelectMany(r => r.Differences).ToArray();

        var verdict = allowlist.Classify(differences);

        Assert.True(verdict.Undeclared.Count == 0, Undeclared(verdict));
    }

    [SkippableFact]
    public void No_allowlist_entry_has_gone_stale()
    {
        var allowlist = MetadataDifferenceAllowlist.Load(MetadataEquivalencePaths.AllowlistFile());
        var verdict = allowlist.Classify(RunAll().SelectMany(r => r.Differences).ToArray());

        Assert.True(verdict.UnusedEntries.Count == 0,
            "these allowlist entries no longer match any difference — the derivation now agrees " +
            "with BC on them, so remove the entries (leaving them grants cover nobody reviewed):" +
            Environment.NewLine + string.Join(Environment.NewLine, verdict.UnusedEntries));

        Assert.True(verdict.ExceededEntries.Count == 0,
            "these declared differences occur more often than the allowlist says — the defect got " +
            "WORSE, which a membership-only allowlist would have hidden:" + Environment.NewLine +
            string.Join(Environment.NewLine, verdict.ExceededEntries));
    }

    [SkippableFact]
    public void The_harness_states_which_kinds_it_does_not_compare()
    {
        // Not decoration. A bundle carries every kind BC emitted; this harness compares
        // tables. If that set silently widened or narrowed, "no differences" would mean
        // something different from run to run.
        foreach (var report in RunAll())
        {
            Assert.Equal(new[] { "MetaTable" }, report.KindsCompared);
            Assert.NotEmpty(report.KindsNotCompared);
            Assert.DoesNotContain("MetaTable", report.KindsNotCompared);
        }
    }

    /// <summary>
    /// Counts measured on one exact app build. Pinned per build because they MOVE with it: the
    /// app's own AL changes between BC releases, so a number measured on 28.1 is not a
    /// prediction about 28.4. A build that is not in here still gets the version-portable
    /// assertions below; adding one is a measurement, not a guess.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, (int Editable, int DataClassification, int EnumTypeId, int Tables)>
        PinnedSystemApplicationCounts = new Dictionary<string, (int, int, int, int)>(StringComparer.Ordinal)
        {
            // Measured by running this harness on each build. They MOVE — DataClassification
            // is 661 / 663 / 617 and EnumTypeId 76 / 76 / 75 across the three below — which is
            // why they are pinned per build rather than asserted as constants.
            ["28.1.49838.53910"] = (78, 661, 76, 138),
            ["28.1.49838.54044"] = (78, 661, 76, 138),
            ["28.4.53241.53989"] = (78, 663, 76, 138),
            ["27.5.46862.53931"] = (78, 617, 75, 127),
        };

    [SkippableFact]
    public void The_current_reader_reproduces_the_known_defect_shapes()
    {
        // This is the test that shows the harness can FAIL. A metadata gate that was green
        // against a reader answering 5,241 wrong Editable values would be proving nothing, so
        // the first thing to establish about this harness is that it sees those defects.
        //
        // Two tiers, deliberately. The portable claims hold on every BC build and are exact —
        // "on EVERY table", not "on some" — so they cannot be satisfied by a differ that has
        // started reporting almost nothing. The pinned counts are the sharper claim and apply
        // only on the build they were measured on.
        var reports = RunAll();

        foreach (var report in reports)
        {
            int MissingRelation(int fieldId) => report.Differences
                .Where(d => d.Member == "Relations." + MetadataObjectDiff.PresenceMember
                            && d.Path.StartsWith($"Fields[id={fieldId}].Relations[", StringComparison.Ordinal)
                            && d.Actual == MetadataObjectDiff.Absent)
                .Select(d => d.ObjectKey).Distinct().Count();

            // BC gives both of these a TableRelation to User (2000000120) and the runner gives
            // them none — on every table, in every app, which is why this is expressed against
            // the number of tables compared rather than as a constant.
            Assert.Equal(report.ObjectsCompared, MissingRelation(2000000002));
            Assert.Equal(report.ObjectsCompared, MissingRelation(2000000004));

            int Declared(string signature) => report.Differences
                .Count(d => d.Signature == signature && IsDeclaredField(d.Path));

            Assert.True(Declared("MetaField.Editable") > 0,
                $"{report.Bundle.Label}: the reader now agrees with BC on Editable for every " +
                "declared field. If #3545 landed, update this test with the new measurement; " +
                "if it did not, the harness has stopped seeing a defect it used to see.");
            Assert.True(Declared("MetaField.DataClassification") > 0,
                $"{report.Bundle.Label}: the reader now agrees with BC on field DataClassification.");
        }

        var systemApp = reports.FirstOrDefault(r => r.Bundle.AppName == "System Application");
        Skip.If(systemApp is null, "no System Application ground truth on this box.");
        if (!PinnedSystemApplicationCounts.TryGetValue(systemApp!.Bundle.AppVersion, out var pinned))
            return;   // a build nobody has measured; the portable claims above still ran

        int DeclaredIn(string signature) => systemApp.Differences
            .Count(d => d.Signature == signature && IsDeclaredField(d.Path));

        Assert.Equal(pinned.Editable, DeclaredIn("MetaField.Editable"));
        Assert.Equal(pinned.DataClassification, DeclaredIn("MetaField.DataClassification"));
        Assert.Equal(pinned.EnumTypeId, DeclaredIn("MetaField.EnumTypeId"));
        Assert.Equal(pinned.Tables, systemApp.ObjectsCompared);
    }

    [SkippableFact]
    public void TranslationKeysAreDerivable_NotAPermanentLimit()
    {
        // Why this test exists rather than a sentence in the allowlist.
        //
        // Seven TranslationKey entries account for 25,721 of the 70,728 differences, and they
        // were first declared as a PERMANENT limit of the symbol file on the evidence that
        // SymbolReference.json contains the string "TranslationKey" zero times. That
        // observation is true and the conclusion drawn from it was wrong: the key is COMPUTED
        // from names, not stored, so "nothing is stored" says nothing about whether anything
        // can be derived. A permanent-limit reason is a licence to differ forever, so the
        // claim underneath it is pinned here instead of asserted in prose.
        //
        // The algorithm is BC's own LanguageKeyHelper.ConstructObjectHash:
        //     (uint)(FNV-1a-32 over the UTF-16LE bytes of the name + int.MaxValue)
        // and a key is "<Kind> <hash>" components joined by " - ", ending in the property.
        //
        // This is evidence, not an implementation. The reader fix (#3568) is where it lands.
        var bundles = MetadataEquivalenceHarness.LoadBundles(
            MetadataEquivalencePaths.GroundTruthDirForThisBuild());
        Skip.If(bundles.Count == 0, "no metadata ground-truth bundle for this BC build.");

        Assert.Equal(2879900210u, TranslationKeyHash("Caption"));
        Assert.Equal(1295455071u, TranslationKeyHash("ToolTip"));
        Assert.Equal(62802879u, TranslationKeyHash("OptionCaption"));

        int attempted = 0, reproduced = 0, tables = 0;
        var counterExamples = new List<string>();
        foreach (var bundle in bundles)
            foreach (var obj in bundle.Objects.Where(o => o.Kind == "MetaTable"))
            {
                var doc = new System.Xml.XmlDocument();
                doc.Load(Path.Combine(bundle.Directory, obj.File));
                var table = doc.DocumentElement!;
                var tableName = table.GetAttribute("Name");
                tables++;

                Check(table.GetAttribute("CaptionTranslationKey"), tableName, null);
                foreach (System.Xml.XmlNode field in table.GetElementsByTagName("Field"))
                {
                    var e = (System.Xml.XmlElement)field;
                    var fieldName = e.GetAttribute("Name");
                    foreach (var attr in new[]
                             { "CaptionTranslationKey", "ToolTipTranslationKey", "OptionCaptionTranslationKey" })
                        Check(e.GetAttribute(attr), tableName, fieldName);
                }

                void Check(string emitted, string owner, string? field)
                {
                    if (string.IsNullOrEmpty(emitted)) return;
                    attempted++;
                    var parts = emitted.Split(" - ", StringSplitOptions.None);
                    var expected = new List<string> { "Table " + TranslationKeyHash(owner) };
                    if (field is not null) expected.Add("Field " + TranslationKeyHash(field));

                    var prefixMatches = parts.Length == expected.Count + 1
                        && expected.Zip(parts).All(p => p.First == p.Second)
                        && parts[^1].StartsWith("Property ", StringComparison.Ordinal);
                    if (prefixMatches) reproduced++;
                    else if (counterExamples.Count < 5)
                        counterExamples.Add($"{obj.Name}: emitted '{emitted}', derived '{string.Join(" - ", expected)} - Property …'");
                }
            }

        Assert.True(counterExamples.Count == 0,
            "the translation-key derivation does not reproduce BC's own keys, so declaring them " +
            "recoverable is wrong and the allowlist reasons must say so:" + Environment.NewLine +
            string.Join(Environment.NewLine, counterExamples));
        // Non-vacuity, measured rather than guessed. An earlier version asserted a flat
        // `> 2000`, which was a number nobody had measured: 27.5's System Application has 127
        // tables against 28.x's 138, so it carries 1,964 keys and the test failed on a run
        // where the derivation had in fact reproduced every single one. The floor now comes
        // from the bundle itself — every table document carries a CaptionTranslationKey, so
        // fewer keys than tables means this stopped finding them.
        Assert.Equal(attempted, reproduced);
        Assert.True(attempted >= tables && tables > 0,
            $"only {attempted} translation keys were found across {tables} table document(s); " +
            "every table carries a CaptionTranslationKey, so this test has stopped measuring " +
            "what it claims to.");
    }

    /// <summary>
    /// BC's <c>LanguageKeyHelper.ConstructObjectHash</c>: FNV-1a-32 over the name's UTF-16LE
    /// bytes, plus <see cref="int.MaxValue"/>, wrapped to <see cref="uint"/>.
    /// </summary>
    private static uint TranslationKeyHash(string name)
    {
        uint hash = 2166136261;
        foreach (var b in System.Text.Encoding.Unicode.GetBytes(name))
            hash = unchecked((hash ^ b) * 16777619);
        return unchecked(hash + int.MaxValue);
    }

    private static bool IsDeclaredField(string path)
    {
        const string marker = "Fields[id=";
        var i = path.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0) return false;
        var j = path.IndexOf(']', i);
        return j > 0
               && int.TryParse(path.AsSpan(i + marker.Length, j - i - marker.Length), out var id)
               && id > 0 && id < 2000000000;
    }

    private static string Undeclared(MetadataAllowlistVerdict verdict)
    {
        var sb = new StringBuilder();
        sb.AppendLine("BC's metadata emitter and the runner's SymbolReference derivation disagree on " +
                      $"{verdict.Undeclared.Count} member(s) that nothing declares. Each is either a " +
                      "reader defect to fix or a difference to declare in " +
                      "tests/expectations/metadata-equivalence/allowlist.json with a reason.");
        foreach (var g in verdict.Undeclared.GroupBy(d => d.Signature)
                     .OrderByDescending(g => g.Count()).Take(40))
            sb.AppendLine($"  {g.Key,-48} x{g.Count(),-6} e.g. {g.First()}");
        return sb.ToString();
    }

    private static void WriteReportIfAsked(IReadOnlyList<MetadataEquivalenceReport> reports)
    {
        var path = Environment.GetEnvironmentVariable("AL_RUNNER_METADATA_EQUIVALENCE_REPORT");
        if (string.IsNullOrEmpty(path)) return;
        var sb = new StringBuilder();
        foreach (var r in reports)
        {
            sb.AppendLine("# " + r.Summary);
            foreach (var d in r.Differences)
                sb.AppendLine($"{r.Bundle.AppName}\t{d.ObjectKey}\t{d.Path}\t{d.Signature}\t{d.Expected}\t{d.Actual}");
        }
        File.WriteAllText(path, sb.ToString());
    }
}
