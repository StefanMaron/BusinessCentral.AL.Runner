// Thin CLI wrapper over AlRunner.Provisioning.ArtifactDownloader.
// The download logic itself lives in the BC-free AlRunner.Provisioning library so the
// runner can call it in-process (auto-provision) and this CLI + the AlRunner MSBuild
// pre-build target can call it standalone. Keep the argument interface stable — CI
// (bc-tests.yml, called by test-matrix.yml and publish.yml) and AlRunner.csproj invoke it as
//   dotnet run --project tools/DownloadArtifacts -- <mode> <version> <output-dir>
//
// Modes:
//   service-tier    <bc-version> <output-dir>   ~55 Nav DLLs (/service/ closure)
//   platform-apps   <bc-version> <output-dir>   MS Base/System/BF/Application .app files
//   test-apps       <bc-version> <output-dir>   test-toolkit .app files
//   al-compiler     <tool-version> <output-dir> AL compiler DLLs (RID-aware NuGet pkg)
//   resolve-version <bc-prefix>                 latest full version → stdout

using AlRunner.Provisioning;

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: DownloadArtifacts service-tier <bc-version> <output-dir>");
    Console.Error.WriteLine("       DownloadArtifacts platform-apps <bc-version> <output-dir>");
    Console.Error.WriteLine("       DownloadArtifacts test-apps <bc-version> <output-dir>");
    Console.Error.WriteLine("       DownloadArtifacts al-compiler <tool-version> <output-dir>");
    Console.Error.WriteLine("       DownloadArtifacts resolve-version <bc-prefix>");
    return 1;
}

var mode = args[0];
var version = args[1];
var outputDir = args.Length >= 3 ? args[2] : "";

switch (mode)
{
    case "service-tier": return ArtifactDownloader.ServiceTier(version, outputDir);
    case "platform-apps": return ArtifactDownloader.PlatformApps(version, outputDir);
    case "test-apps": return ArtifactDownloader.TestApps(version, outputDir);
    case "al-compiler": return ArtifactDownloader.AlCompiler(version, outputDir);
    case "resolve-version":
        var resolved = ArtifactDownloader.ResolveVersion(version);
        if (resolved == null) return 1;
        Console.WriteLine(resolved); // stdout for script consumption
        return 0;
    default:
        Console.Error.WriteLine($"Error: Unknown mode: {mode}");
        return 1;
}
