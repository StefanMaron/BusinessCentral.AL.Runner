// PublishReleaseOrderingTests — regression guard for #2010: the v2.4.0 release passed all 8
// BC test-matrix legs, then failed in the release job's "Build and pack" step (a withdrawn BC
// artifact), after the CHANGELOG commit and the `v2.4.0` tag had already been pushed to main —
// because that step ran AFTER the push, not before it, and the pin it built against was a
// static value nothing exercised except a release.
//
// Two things this pins down, both as pure functions of the real workflow text so a future
// reordering (or a reintroduced static pin) fails a normal unit-test run instead of the next
// release:
//
//   1. Within publish.yml's `release` job, "Build and pack" appears BEFORE the step that
//      pushes the CHANGELOG commit and tag to origin, which appears before "Push to NuGet" and
//      "Create GitHub Release". A build/pack failure must be unreachable-after-a-write.
//   2. The build/pack step passes `-p:_BCVersion=` a value derived from
//      `needs.test.outputs.required-version` (the live-resolved version the matrix this run
//      just gated on), not a hardcoded four-part BC build number. bc-tests.yml's
//      `workflow_call` block actually exposes that output — a caller reading an output the
//      reusable workflow never declares silently gets an empty string, not an error.
//
// See AlRunner.Tests/ReleaseTestParityTests.cs for the sibling guard (test-matrix parity) this
// follows the same shape as, and AlRunner/AlRunner.csproj's `_BCVersion` PropertyGroup comment
// for why the static default staying in the csproj is fine now: nothing in CI or the release
// path reads it anymore.

using System.Text.RegularExpressions;
using Xunit;

namespace AlRunner.Tests;

/// <summary>Pure helpers over workflow YAML text, proven on constructed strings before being
/// wired to the real files.</summary>
internal static class PublishOrdering
{
    private static readonly Regex StepName = new(@"^\s*-\s*(?:id:\s*\S+\s*)?name:\s*(.+?)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// The order (top to bottom) in which step names matching <paramref name="markers"/> first
    /// appear in <paramref name="jobBody"/>, matched by substring so callers can pass a stable
    /// fragment rather than the exact step name. A marker with no matching step is omitted —
    /// callers assert on <c>Count</c> to catch that rather than silently ignoring it.
    /// </summary>
    internal static IReadOnlyList<string> StepOrder(string jobBody, IReadOnlyList<string> markers)
    {
        var found = new List<string>();
        foreach (var line in jobBody.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#') || !trimmed.StartsWith("- name:", StringComparison.Ordinal)) continue;

            var match = StepName.Match(line);
            if (!match.Success) continue;
            var name = match.Groups[1].Value;

            var marker = markers.FirstOrDefault(m => name.Contains(m, StringComparison.Ordinal));
            if (marker is not null && !found.Contains(marker)) found.Add(marker);
        }
        return found;
    }

    /// <summary>
    /// True if <paramref name="jobBody"/>'s "Build and pack" step (or whatever step contains
    /// <paramref name="buildStepMarker"/>) passes <c>-p:_BCVersion=</c> a value that references
    /// <paramref name="expectedSource"/>, rather than a bare hardcoded four-part BC build number
    /// (e.g. <c>28.1.49838.50794</c>) which is exactly the shape that rotted in #2010.
    /// </summary>
    internal static bool BuildStepResolvesBcVersionFrom(string jobBody, string buildStepMarker, string expectedSource)
    {
        var lines = jobBody.Split('\n');
        var inStep = false;
        foreach (var line in lines)
        {
            if (line.Contains(buildStepMarker, StringComparison.Ordinal)) inStep = true;
            else if (inStep && Regex.IsMatch(line, @"^\s*- (name|id):", RegexOptions.None) && !line.Contains(buildStepMarker, StringComparison.Ordinal))
                break; // next step started

            if (!inStep) continue;
            if (!line.Contains("_BCVersion=", StringComparison.Ordinal)) continue;

            if (Regex.IsMatch(line, @"_BCVersion=\d+\.\d+\.\d+\.\d+")) return false; // hardcoded literal
            if (line.Contains(expectedSource, StringComparison.Ordinal)) return true;
        }
        return false;
    }
}

public sealed class PublishReleaseOrderingTests
{
    private static readonly string WorkflowDir = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".github", "workflows"));

    private static string Read(string name)
    {
        var path = Path.Combine(WorkflowDir, name);
        Assert.True(File.Exists(path), $"expected workflow {name} at {path}");
        return File.ReadAllText(path);
    }

    // ---- the pure functions, proven on constructed text ------------------------------

    [Fact]
    public void StepOrder_ReturnsMarkers_InDocumentOrder_NotDeclarationOrder()
    {
        const string job = """
              release:
                steps:
                  - name: Push to NuGet
                    run: dotnet nuget push
                  - name: Build and pack
                    run: dotnet pack
            """;

        var order = PublishOrdering.StepOrder(job, new[] { "Push to NuGet", "Build and pack" });

        Assert.Equal(new[] { "Push to NuGet", "Build and pack" }, order);
    }

    [Fact]
    public void StepOrder_IgnoresCommentedOutStepNames()
    {
        const string job = """
              release:
                steps:
                  # - name: Build and pack (old, disabled)
                  - name: Generate CHANGELOG and push the release commit + tag
                    run: git push
            """;

        var order = PublishOrdering.StepOrder(job, new[] { "Build and pack", "Generate CHANGELOG" });

        Assert.Equal(new[] { "Generate CHANGELOG" }, order);
    }

    [Fact]
    public void BuildStepResolvesBcVersionFrom_RejectsAHardcodedFourPartVersion()
    {
        const string job = """
              release:
                steps:
                  - name: Build and pack
                    run: |
                      dotnet build AlRunner/ -p:_BCVersion=28.1.49838.50794 -p:AllowBcArtifactDownload=true
            """;

        Assert.False(PublishOrdering.BuildStepResolvesBcVersionFrom(job, "Build and pack", "needs.test.outputs.required-version"));
    }

    [Fact]
    public void BuildStepResolvesBcVersionFrom_AcceptsALiveResolvedSource()
    {
        const string job = """
              release:
                steps:
                  - name: Build and pack
                    run: |
                      dotnet build AlRunner/ -p:_BCVersion=${{ needs.test.outputs.required-version }} -p:AllowBcArtifactDownload=true
            """;

        Assert.True(PublishOrdering.BuildStepResolvesBcVersionFrom(job, "Build and pack", "needs.test.outputs.required-version"));
    }

    // ---- wired to the real files on disk ----------------------------------------------

    [Fact]
    public void ReleaseJob_BuildsAndPacks_BeforeItPushesTheCommitAndTag_BeforeNuGetAndGitHubRelease()
    {
        var text = Read("publish.yml");
        var jobs = WorkflowParity.SplitJobs(text);
        Assert.True(jobs.TryGetValue("release", out var releaseJob), "publish.yml has no top-level 'release' job");

        var markers = new[]
        {
            "Build and pack",
            "Generate CHANGELOG and push the release commit + tag",
            "Push to NuGet",
            "Create GitHub Release",
        };

        var order = PublishOrdering.StepOrder(releaseJob!, markers);

        Assert.True(order.Count == markers.Length,
            $"expected all four release-job steps present in order; found: {string.Join(" -> ", order)}");
        Assert.Equal(markers, order);
    }

    [Fact]
    public void ReleaseJob_BuildAndPackStep_ResolvesBcVersionFromTheGatedMatrix_NotAHardcodedPin()
    {
        // #2010: the pin that rotted (AlRunner.csproj's static default _BCVersion) is still a
        // legitimate DEV-MACHINE fallback (see its own comment), but the release job must never
        // read it implicitly — it has to pass a version it can prove, this run, still exists.
        var text = Read("publish.yml");
        var jobs = WorkflowParity.SplitJobs(text);
        Assert.True(jobs.TryGetValue("release", out var releaseJob), "publish.yml has no top-level 'release' job");

        Assert.True(
            PublishOrdering.BuildStepResolvesBcVersionFrom(releaseJob!, "Build and pack", "needs.test.outputs.required-version"),
            "release job's 'Build and pack' step must pass -p:_BCVersion=${{ needs.test.outputs.required-version }}, "
            + "not a hardcoded four-part BC build number — that is exactly what rotted and broke v2.4.0.");
    }

    [Fact]
    public void BcTestsWorkflow_ExposesRequiredVersion_AsAWorkflowCallOutput()
    {
        // The release job reads needs.test.outputs.required-version, which only resolves to
        // something non-empty if bc-tests.yml's `on.workflow_call` block actually declares
        // `required-version` as a top-level output (jobs' own `outputs:` blocks are NOT
        // automatically visible to callers of a reusable workflow).
        var text = Read("bc-tests.yml");

        var callBlock = Regex.Match(text, @"on:\s*\n\s*workflow_call:.*?(?=\nenv:|\njobs:)", RegexOptions.Singleline);
        Assert.True(callBlock.Success, "bc-tests.yml has no on.workflow_call block");

        Assert.Matches(new Regex(@"outputs:\s*\n\s*required-version:", RegexOptions.Singleline), callBlock.Value);
        Assert.Contains("jobs.resolve-versions.outputs.required-version", callBlock.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void BcTestsWorkflow_HasAPackJob_ThatBuildsAndPacksOnOrdinaryCI()
    {
        // #2010 acceptance criterion 6: the release path's build must be exercised by ordinary
        // CI, so a build/pack regression (BC-version rot or otherwise) surfaces on a normal
        // push/PR run instead of during a release. This is the job that does that — it runs the
        // same `dotnet build`/`dotnet pack` commands as publish.yml's release job, against the
        // same live-resolved version, without publishing anything.
        var text = Read("bc-tests.yml");
        var jobs = WorkflowParity.SplitJobs(text);
        Assert.True(jobs.TryGetValue("pack", out var packJob), "bc-tests.yml has no top-level 'pack' job");

        Assert.Contains("dotnet build AlRunner/", packJob, StringComparison.Ordinal);
        Assert.Contains("dotnet pack AlRunner/", packJob, StringComparison.Ordinal);
        Assert.Contains("needs.resolve-versions.outputs.required-version", packJob, StringComparison.Ordinal);

        // Must never push anywhere — this job proves the build works, it does not publish it.
        Assert.DoesNotContain("nuget push", packJob, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("action-gh-release", packJob, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublishWorkflow_HasADryRunInput_ThatSkipsOnlyNuGetPushAndGitHubRelease()
    {
        var text = Read("publish.yml");

        Assert.Contains("dry_run:", text, StringComparison.Ordinal);

        var jobs = WorkflowParity.SplitJobs(text);
        Assert.True(jobs.TryGetValue("release", out var releaseJob), "publish.yml has no top-level 'release' job");

        // Exactly the two irreversible/external steps are gated on dry_run being false.
        var pushToNuGet = ExtractStep(releaseJob!, "Push to NuGet");
        var createRelease = ExtractStep(releaseJob!, "Create GitHub Release");
        Assert.Contains("inputs.dry_run", pushToNuGet, StringComparison.Ordinal);
        Assert.Contains("inputs.dry_run", createRelease, StringComparison.Ordinal);

        // Build and pack must NOT be skipped in dry_run — it's the step the dry run exists to
        // exercise.
        var buildAndPack = ExtractStep(releaseJob!, "Build and pack");
        Assert.DoesNotContain("if:", buildAndPack, StringComparison.Ordinal);
    }

    private static string ExtractStep(string jobBody, string stepNameMarker)
    {
        var lines = jobBody.Split('\n');
        var start = Array.FindIndex(lines, l => l.TrimStart().StartsWith("- name:", StringComparison.Ordinal)
                                                  && l.Contains(stepNameMarker, StringComparison.Ordinal));
        Assert.True(start >= 0, $"no step named like '{stepNameMarker}' found");

        var end = lines.Length;
        for (var i = start + 1; i < lines.Length; i++)
        {
            if (lines[i].TrimStart().StartsWith("- name:", StringComparison.Ordinal)
                || lines[i].TrimStart().StartsWith("- uses:", StringComparison.Ordinal))
            {
                end = i;
                break;
            }
        }
        return string.Join('\n', lines[start..end]);
    }
}
