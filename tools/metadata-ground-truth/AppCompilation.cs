// Configuring BC's own compiler over a shipped Microsoft app's source, the way BC's publish
// step does. Every knob here exists because omitting it changes the emitted metadata or stops
// the compile outright; each is noted at its line.

using NavCA = Microsoft.Dynamics.Nav.CodeAnalysis;

namespace AlRunner.Tools.MetadataGroundTruth;

internal static class AppCompilation
{
    public static NavCA.Compilation Create(
        AppIdentity identity, string packageRoot, string srcRoot, string serviceTierDir, string appPath,
        IReadOnlyList<string>? dotnetFirst = null)
    {
        var features = NavCA.CompilerFeatures.None;
        foreach (var f in identity.Features)
            if (Enum.TryParse<NavCA.CompilerFeatures>(f, ignoreCase: true, out var parsed))
                features |= parsed;

        if (!Enum.TryParse<NavCA.CompilationTarget>(identity.Target, ignoreCase: true, out var target))
            target = NavCA.CompilationTarget.OnPrem;

        var parseOptions = identity.PreprocessorSymbols.Count > 0
            ? NavCA.ParseOptions.Default.WithPreprocessorSymbols(identity.PreprocessorSymbols)
            : NavCA.ParseOptions.Default;

        var files = Directory.GetFiles(srcRoot, "*.al", SearchOption.AllDirectories);
        Console.WriteLine($"[src] {files.Length} .al files");
        var trees = files.AsParallel()
            .Select(f => NavCA.Syntax.SyntaxTree.ParseObjectText(
                LayoutPaths.Normalize(File.ReadAllText(f)), path: f, encoding: null, parseOptions, default))
            .ToArray();

        var options = new NavCA.CompilationOptions(
            continueBuildOnError: true,
            target: target,
            // Code | Navigation is what publish asks for. Metadata is emitted alongside code
            // and is not separately selectable, so asking for less than Code yields no
            // metadata documents at all.
            generateOptions: NavCA.CompilationGenerationOptions.Code | NavCA.CompilationGenerationOptions.Navigation,
            compilerFeatures: features,
            contextSensitiveHelpUrl: string.IsNullOrEmpty(identity.ContextSensitiveHelpUrl)
                ? null : identity.ContextSensitiveHelpUrl);

        var comp = NavCA.Compilation.Create(
            moduleName: identity.Name, publisher: identity.Publisher, version: identity.Version,
            appId: identity.Id, syntaxTrees: trees, options: options);

        comp = comp.WithFileSystem(new LayeredFileSystem(ResourceRoots(packageRoot)));
        comp = WithReferences(comp, identity, serviceTierDir, appPath);
        comp = comp.WithDotNetResolverFactory(
            new NavCA.DotNet.DotNetResolverFactory(
                new NavCA.DotNet.AssemblyLocator(DotNetProbingPaths(serviceTierDir, dotnetFirst))));
        return comp;
    }

    /// <summary>
    /// Resource lookups resolve relative to the package root, to each directory it ships, and
    /// to addin/src — a control add-in's files sit under the last of those and nowhere the
    /// package root alone would find them.
    /// </summary>
    private static IEnumerable<string> ResourceRoots(string packageRoot)
    {
        yield return packageRoot;
        foreach (var sub in Directory.GetDirectories(packageRoot)) yield return sub;
        yield return Path.Combine(packageRoot, "addin", "src");
    }

    private static NavCA.Compilation WithReferences(
        NavCA.Compilation comp, AppIdentity identity, string serviceTierDir, string appPath)
    {
        // Every reference is resolved from .app packages sitting beside the subject app, plus
        // the platform's own System.app. An app declaring no <Dependencies> still depends on
        // the platform — System Application is exactly that case — so the platform app is
        // added unconditionally rather than only when the manifest mentions it.
        var appDir = Path.GetDirectoryName(Path.GetFullPath(appPath))!;
        var searchDirs = new List<string> { appDir };
        foreach (var name in new[] { "platform-apps", "platform-apps-us", "Applications", "Extensions" })
        {
            var d = Path.Combine(serviceTierDir, name);
            if (Directory.Exists(d) && !searchDirs.Contains(d)) searchDirs.Add(d);
        }

        var wanted = new List<(Guid Id, string Name, string Publisher, Version Version)>(identity.Dependencies);
        var specs = new List<NavCA.SymbolReferenceSpecification>();
        var resolved = new List<string>();
        var refDir = Directory.CreateTempSubdirectory("al-runner-groundtruth-refs-");

        foreach (var candidate in searchDirs.SelectMany(d => Directory.GetFiles(d, "*.app")))
        {
            if (string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(appPath), StringComparison.Ordinal))
                continue;
            AppIdentity id;
            try { id = NavxPackage.ReadIdentity(candidate); }
            catch { continue; }   // not a readable .app; the reference loader would reject it too

            var isPlatform = string.Equals(id.Name, "System", StringComparison.OrdinalIgnoreCase);
            var isDeclared = wanted.Any(w => w.Id == id.Id);
            if (!isPlatform && !isDeclared) continue;

            var staged = Path.Combine(refDir.FullName, Path.GetFileName(candidate));
            if (File.Exists(staged)) continue;
            File.Copy(candidate, staged);
            specs.Add(new NavCA.SymbolReferenceSpecification(
                id.Name, id.Publisher, id.Version, false, id.Id));
            resolved.Add($"{id.Name} {id.Version}");
        }

        if (specs.Count == 0)
            throw new InvalidOperationException(
                $"no reference packages found for {identity.Name}. Looked in: {string.Join(", ", searchDirs)}. " +
                "Without the platform's System.app the compile produces thousands of AL0185s and " +
                "no metadata, which would otherwise look like an app-specific failure.");

        Console.WriteLine($"[refs] {specs.Count}: " + string.Join(", ", resolved));
        var loader = NavCA.SymbolReference.ReferenceLoaderFactory
            .CreateReferenceLoader(new[] { refDir.FullName });
        return comp.WithReferenceLoader(loader).AddReferences(specs.ToArray());
    }

    /// <summary>
    /// The same order the runner's own compiler setup uses: .NET REFERENCE packs first (real
    /// TypeDefinitions rather than forwarder facades), then the service tier, then its
    /// Add-ins, then the shared framework LAST. Putting the shared framework first resolves
    /// facades and the AL DotNet declarations fail to bind.
    /// </summary>
    private static List<string> DotNetProbingPaths(string serviceTierDir, IReadOnlyList<string>? dotnetFirst)
    {
        var probing = new List<string>();
        // Shipped beside this tool by the CopyDotNetShims target; see the csproj for what is in
        // it and why it has to come first.
        var shims = Path.Combine(AppContext.BaseDirectory, "dotnet-shims");
        if (Directory.Exists(shims)) probing.Add(shims);
        foreach (var d in dotnetFirst ?? Array.Empty<string>())
            if (Directory.Exists(d)) probing.Add(d);
        var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
        var idx = runtimeDir.Replace('\\', '/').IndexOf("/shared/", StringComparison.OrdinalIgnoreCase);
        var dotnetRoot = idx > 0 ? runtimeDir[..idx] : null;

        if (dotnetRoot is not null && Directory.Exists(Path.Combine(dotnetRoot, "packs")))
        {
            var packs = Path.Combine(dotnetRoot, "packs");
            int? runtimeMajor = Version.TryParse(Path.GetFileName(runtimeDir.TrimEnd('/')), out var v) ? v.Major : null;
            AddRefPack(probing, Path.Combine(packs, "Microsoft.NETCore.App.Ref"), runtimeMajor);
            AddRefPack(probing, Path.Combine(packs, "NETStandard.Library.Ref"), null);
        }

        probing.Add(serviceTierDir);
        var addins = Path.Combine(serviceTierDir, "Add-ins");
        if (Directory.Exists(addins))
            probing.AddRange(Directory.GetDirectories(addins, "*", SearchOption.AllDirectories));
        var webClient = Path.Combine(serviceTierDir, "platform", "WebClient");
        if (Directory.Exists(webClient))
            probing.AddRange(Directory.GetDirectories(webClient, "*", SearchOption.AllDirectories)
                .Where(d => Path.GetFileName(d) == "refs"));
        probing.Add(runtimeDir);
        return probing;
    }

    private static void AddRefPack(List<string> probing, string packRoot, int? preferMajor)
    {
        if (!Directory.Exists(packRoot)) return;
        var candidates = Directory.EnumerateDirectories(packRoot)
            .Select(d => (Dir: d, Ver: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .Where(t => t.Ver is not null).ToList();
        var best = candidates.Where(t => preferMajor is null || t.Ver!.Major == preferMajor.Value)
                       .OrderByDescending(t => t.Ver).Select(t => t.Dir).FirstOrDefault()
                   ?? candidates.OrderByDescending(t => t.Ver).Select(t => t.Dir).FirstOrDefault();
        if (best is null) return;
        var refDir = Path.Combine(best, "ref");
        if (Directory.Exists(refDir)) probing.AddRange(Directory.EnumerateDirectories(refDir));
    }
}

/// <summary>
/// Layout property values ship with Windows separators in some Microsoft apps and raise AL0363
/// on Linux. Normalizing them in the source text is what the runner's own compile path does.
/// </summary>
internal static class LayoutPaths
{
    private static readonly System.Text.RegularExpressions.Regex Rx = new(
        @"(?im)^(\s*(?:LayoutFile|RDLCLayout|WordLayout|ExcelLayout)\s*=\s*')([^']*)(')");

    public static string Normalize(string text)
        => text.IndexOf('\\') < 0 ? text : Rx.Replace(text, m =>
            m.Groups[1].Value + m.Groups[2].Value.Replace('\\', '/') + m.Groups[3].Value);
}
