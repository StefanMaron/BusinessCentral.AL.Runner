using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #5150: a compile narrowed to its declared references (#4096) reads two things no other key
/// line carries — the app's own <c>dependencies</c> and each dependency's
/// <c>propagateDependencies</c>. Every cache over such a compile must key on them, or a result
/// compiled under one workspace's declarations answers for another's. Fresh app ids per test,
/// so the process-wide declaration record cannot leak between facts.
/// </summary>
public sealed class DeclaredVisibilityCacheKeyTests
{
    private sealed record App(Guid Id, string Name, string Dir);

    private static App WriteApp(string root, string name, bool propagates, params App[] dependencies)
    {
        var app = new App(Guid.NewGuid(), name, Path.Combine(root, name.Replace(' ', '-')));
        Directory.CreateDirectory(app.Dir);
        WriteManifest(app, propagates, dependencies);
        File.WriteAllText(Path.Combine(app.Dir, "Object.Codeunit.al"),
            $"codeunit {60090 + name.Length} \"{name}\" {{ }}");
        return app;
    }

    // Writes the app.json and records it, as every source-app compile path does.
    private static void WriteManifest(App app, bool propagates, params App[] dependencies)
    {
        var deps = string.Join(", ", dependencies.Select(d =>
            $$"""{ "id": "{{d.Id}}", "name": "{{d.Name}}", "publisher": "AL Runner", "version": "1.0.0.0" }"""));
        var path = Path.Combine(app.Dir, "app.json");
        File.WriteAllText(path, $$"""
        { "id": "{{app.Id}}", "name": "{{app.Name}}", "publisher": "AL Runner", "version": "1.0.0.0",
          "dependencies": [ {{deps}} ], "propagateDependencies": {{(propagates ? "true" : "false")}},
          "platform": "1.0.0.0", "runtime": "14.0" }
        """);
        Assert.NotNull(InProcessAppPackager.ReadIdentity(path));
    }

    private static string AlOutputKey(App app)
        => ProgramSupport.ComputeAlCacheKey(new[] { app.Dir }, "VisibilityKey", Array.Empty<string>(), app.Dir);

    [SkippableFact]
    public void AlOutputKey_FollowsTheDependencysPropagation()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("visibility-key-al-output-propagation");
        var baseApp = WriteApp(root, "Base", propagates: false);
        var middle = WriteApp(root, "Middle", propagates: true, baseApp);
        var test = WriteApp(root, "Test", propagates: false, middle);

        var propagating = AlOutputKey(test);
        WriteManifest(middle, propagates: false, baseApp);
        var notPropagating = AlOutputKey(test);
        WriteManifest(middle, propagates: true, baseApp);
        var propagatingAgain = AlOutputKey(test);

        Assert.Matches("^[0-9a-f]{64}$", propagating);
        Assert.NotEqual(propagating, notPropagating);
        Assert.Equal(propagating, propagatingAgain);
    }

    [SkippableFact]
    public void AlOutputKey_FollowsTheAppsOwnDeclarations()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("visibility-key-al-output-declarations");
        var baseApp = WriteApp(root, "Base", propagates: false);
        var middle = WriteApp(root, "Middle", propagates: false, baseApp);
        var test = WriteApp(root, "Test", propagates: false, middle, baseApp);

        var declaresBoth = AlOutputKey(test);
        WriteManifest(test, propagates: false, middle);
        var declaresMiddleOnly = AlOutputKey(test);

        Assert.NotEqual(declaresBoth, declaresMiddleOnly);
    }

    [SkippableFact]
    public void SourceWorkspaceKey_FollowsTheDependencysPropagation()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("visibility-key-source-workspace");
        var rootApp = WriteApp(root, "Root", propagates: false);
        var baseApp = WriteApp(root, "Base", propagates: true, rootApp);
        var middle = WriteApp(root, "Middle", propagates: false, baseApp);
        var sourceApps = new Dictionary<string, BundleIdentity>(StringComparer.OrdinalIgnoreCase)
        {
            [middle.Dir] = InProcessAppPackager.ReadIdentity(Path.Combine(middle.Dir, "app.json"))!,
        };
        string Key() => ProgramSupport.ComputeSourceWorkspaceKey(
            new[] { middle.Dir }, sourceApps, Array.Empty<(AppManifest, string)>(), resolutionFailure: null);

        var propagating = Key();
        WriteManifest(baseApp, propagates: false, rootApp);
        var notPropagating = Key();

        Assert.NotEqual(propagating, notPropagating);
    }

    [SkippableFact]
    public void DependencySourceKey_FollowsTheDependencysPropagation()
    {
        TestArtifacts.SkipIfMissing();
        var root = TestScratch.Dir("visibility-key-dependency-source");
        var rootApp = WriteApp(root, "Root", propagates: false);
        var baseApp = WriteApp(root, "Base", propagates: true, rootApp);
        var middle = WriteApp(root, "Middle", propagates: false, baseApp);
        var manifest = new AppManifest("AL Runner", middle.Name, new Version(1, 0, 0, 0), middle.Id,
            new[] { new DependencyRef(baseApp.Id, baseApp.Name, "AL Runner", new Version(1, 0, 0, 0)) });
        var package = Path.Combine(root, "middle.app");
        File.WriteAllText(package, "package bytes");
        string Key() => DependencyLoader.ComputeSourceDependencyCacheKeyCore(manifest, package, _ => "same-bytes");

        var propagating = Key();
        WriteManifest(baseApp, propagates: false, rootApp);
        var notPropagating = Key();

        Assert.NotEqual(propagating, notPropagating);
    }
}
