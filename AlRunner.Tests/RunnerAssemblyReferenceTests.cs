// RunnerAssemblyReferenceTests — the mechanism half of issue #2880.
//
// WHAT #2880 OBSERVED
//   Two consecutive tests/runner-extras runs, identical tree, identical build. The first one's
//   consolidated standalone-suites module failed to compile with 24 errors, all rooted in:
//
//     _polyfill.cs(31,24): error CS0400: The type or namespace name 'AlRunner' could not be
//                          found in the global namespace (are you missing an assembly reference?)
//     _polyfill.cs(323,18): error CS8130: Cannot infer the type of implicitly-typed
//                          deconstruction variable 'appId'.
//
//   The second run compiled it and reported 265/265.
//
// WHAT THOSE COORDINATES IDENTIFY
//   They are not approximate. BcAssembler parses PolyfillSource with path "_polyfill.cs", so
//   Roslyn's line/column are offsets into that string literal:
//
//     line  31, col 24 → `            => global::AlRunner.BcRuntime.NCLEnumMetadata_CreateByIdAlAware(id);`
//                         (12 spaces + "=> global::" is 23 characters; `AlRunner` starts at 24)
//     line 323, col 18 → `            var (appId, name, publisher, version) = global::AlRunner…`
//                         (`appId` starts at 18, and CS8130 names 'appId')
//
//   PolyfillReferenceCoordinatesTests below pins both, so this diagnosis cannot rot into a
//   guess if the polyfill is edited.
//
//   CS0400 on `global::AlRunner` means one thing: the compilation had NO reference defining the
//   `AlRunner` namespace. Not a corrupt reference (that is CS0009 / a BadImageFormatException),
//   not a version skew (that is CS0117 / CS1061 on the member) — absent. The deconstruction
//   errors are the downstream consequence, since the tuple's type comes from a method on a type
//   that no longer resolves.
//
// WHERE AN ABSENT REFERENCE COMES FROM
//   Exactly one place. BcAssembler.ReferencePaths ended with:
//
//     var runnerDll = typeof(BcAssembler).Assembly.Location;
//     if (!string.IsNullOrEmpty(runnerDll) && File.Exists(runnerDll))
//         yield return runnerDll;
//
//   Every other entry in that list is genuinely optional — a BC version that does not ship
//   Microsoft.Dynamics.Nav.Types.Report.Runtime.dll should compile without it. The runner's own
//   assembly is not optional: PolyfillSource references `global::AlRunner` unconditionally, so a
//   compilation without it cannot succeed, ever. The `File.Exists` guard turned "the one
//   mandatory reference is unavailable" into silence, and the compile then failed 24 lines
//   later in a way that names AL sources and a namespace rather than the missing reference.
//
// WHAT IS *NOT* ESTABLISHED, AND IS NOT ASSERTED ANYWHERE HERE
//   Why `Location` was empty or `File.Exists` false on that one run. #2880's own hypothesis is a
//   concurrent `dotnet build` replacing bin/al-runner.dll mid-run — MSBuild's copy is delete-
//   then-write, so the path is genuinely absent for a moment. Plausible, unreproduced, and
//   deliberately not encoded as a test: .claude/rules/no-assumption-fixes.md. These tests assert
//   only what is measurable — that the reference is mandatory, that its absence is now named
//   instead of silent, and that a momentarily-unreadable file can no longer remove it.
//
// Runner infrastructure throughout. Nothing here is a claim about Business Central.

using System;
using System.IO;
using System.Linq;
using Xunit;

namespace AlRunner.Tests;

public sealed class RunnerAssemblyReferenceTests
{
    /// <summary>
    /// The reference is present under normal conditions. Without this every negative below is
    /// satisfied by a build in which the runner assembly is never referenced at all.
    /// </summary>
    [Fact]
    public void ReferencePaths_IncludeTheRunnersOwnAssembly()
    {
        var paths = new BcAssembler().ReferencePathsForTests().ToList();

        var runnerDll = typeof(BcAssembler).Assembly.Location;
        Assert.Contains(runnerDll, paths, StringComparer.Ordinal);
    }

    /// <summary>
    /// The fix's core claim. Resolution happens once per process and the result is held, so a
    /// later moment in which the file is unreadable — the shape #2880 points at — cannot remove
    /// the reference from a compilation. Asserts the same instance, not merely an equal path:
    /// re-reading the path is exactly the behaviour being removed.
    /// </summary>
    [Fact]
    public void RunnerAssemblyReference_IsResolvedOnceAndHeldForTheProcess()
    {
        var first = BcAssembler.RunnerAssemblyReference;
        var second = BcAssembler.RunnerAssemblyReference;

        Assert.NotNull(first);
        Assert.Same(first, second);
    }

    /// <summary>
    /// A transient must stay transient. Review of #2898 found the held reference behind a plain
    /// <c>Lazy&lt;T&gt;</c>, whose default <c>ExecutionAndPublication</c> mode CACHES the
    /// factory's exception: one unreadable moment at the first compile of the process would make
    /// every later compile rethrow it, long after the file came back. That converts the exact
    /// momentary condition #2880 points at into a permanent one — the opposite of the fix.
    /// <c>PublicationOnly</c> retries instead, which is asserted here as behaviour rather than
    /// as the value of an enum.
    /// </summary>
    [Fact]
    public void RunnerAssemblyReferenceHolder_AfterATransientFailure_RetriesInsteadOfCachingIt()
    {
        int calls = 0;
        var holder = BcAssembler.NewRunnerAssemblyReferenceHolderForTests(() =>
        {
            calls++;
            if (calls == 1) throw new IOException("the file is being replaced right now");
            return Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(
                typeof(BcAssembler).Assembly.Location);
        });

        Assert.Throws<IOException>(() => _ = holder.Value);
        var second = holder.Value;          // the file came back — so must the reference
        Assert.NotNull(second);
        Assert.Same(second, holder.Value);  // and once resolved it is held, as before
        Assert.Equal(2, calls);
    }

    /// <summary>
    /// The loud-failure half (.claude/rules/loud-failures.md). A mandatory reference that cannot
    /// be resolved must say so, naming the assembly and what it would have broken — not fall
    /// through to 24 CS0400s in a generated file the reader has never seen.
    /// </summary>
    [Fact]
    public void ResolveRunnerAssemblyPath_WhenTheAssemblyIsUnavailable_ThrowsNamingIt()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BcAssembler.ResolveRunnerAssemblyPathForTests(location: "", exists: _ => false));

        Assert.Contains("AlRunner", ex.Message, StringComparison.Ordinal);
        Assert.Contains("_polyfill.cs", ex.Message, StringComparison.Ordinal);
        Assert.Contains("#2880", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Same, for the case #2880 actually points at: the path is known, the file is momentarily
    /// not there. Previously this returned nothing and the caller carried on.
    /// </summary>
    [Fact]
    public void ResolveRunnerAssemblyPath_WhenTheFileIsMissing_ThrowsInsteadOfReturningNothing()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => BcAssembler.ResolveRunnerAssemblyPathForTests(
                location: "/nonexistent/al-runner.dll", exists: _ => false));

        Assert.Contains("/nonexistent/al-runner.dll", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The negative control. A readable file resolves normally and throws nothing — an
    /// implementation that always threw would satisfy both tests above.
    /// </summary>
    [Fact]
    public void ResolveRunnerAssemblyPath_WhenTheFileIsPresent_ReturnsIt()
    {
        var resolved = BcAssembler.ResolveRunnerAssemblyPathForTests(
            location: "/some/al-runner.dll", exists: _ => true);

        Assert.Equal("/some/al-runner.dll", resolved);
    }
}

/// <summary>
/// Pins the two polyfill coordinates #2880's error list named, so the diagnosis in this file's
/// header stays checkable against the source rather than becoming folklore. If the polyfill is
/// edited these move — that is fine and the test says what they moved to; what it prevents is
/// the header quietly describing lines that no longer say what it claims.
/// </summary>
public sealed class PolyfillReferenceCoordinatesTests
{
    private static string[] PolyfillLines() =>
        BcAssembler.PolyfillSourceForTests.Replace("\r\n", "\n").Split('\n');

    [Fact]
    public void PolyfillLine31Column24_IsTheGlobalAlRunnerReferenceCs0400Named()
    {
        var lines = PolyfillLines();
        // Roslyn line numbers are 1-based; the verbatim literal's first line is the empty
        // remainder after the opening quote, so string line N is lines[N - 1].
        var line31 = lines[30];

        Assert.Contains("global::AlRunner.BcRuntime.NCLEnumMetadata_CreateByIdAlAware",
            line31, StringComparison.Ordinal);
        // Column 24, 1-based, is where `AlRunner` begins — the token CS0400 could not resolve.
        Assert.Equal("AlRunner", line31.Substring(23, "AlRunner".Length));
    }

    [Fact]
    public void PolyfillLine323Column18_IsTheDeconstructionVariableCs8130Named()
    {
        var lines = PolyfillLines();
        var line323 = lines[322];

        Assert.Contains("global::AlRunner.BcRuntime.GetModuleAppInfoFor",
            line323, StringComparison.Ordinal);
        // CS8130 named 'appId'; column 18, 1-based.
        Assert.Equal("appId", line323.Substring(17, "appId".Length));
    }

    /// <summary>
    /// The property that makes the runner assembly mandatory rather than optional: the polyfill
    /// references it unconditionally, so no compilation can succeed without it. If this ever
    /// stops being true the loud failure above is over-strict and should be revisited.
    /// </summary>
    [Fact]
    public void PolyfillSource_ReferencesTheAlRunnerNamespaceUnconditionally()
    {
        var count = BcAssembler.PolyfillSourceForTests
            .Split("global::AlRunner.", StringSplitOptions.None).Length - 1;

        Assert.True(count >= 10, $"expected the polyfill to lean on AlRunner throughout; found {count}");
    }
}
