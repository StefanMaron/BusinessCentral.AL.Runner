// InProcessAppPackagerReproducibleTests — issue #5306.
//
// A source bundle's package is what the compiled-deps key reads as that dependency's identity, so the
// same sources must give the same bytes. ZipArchive stamps each entry with the clock at two-second
// resolution, which made two packagings of one bundle equal or different by when they ran: under --tdd
// the passes of a run package the regenerated bundles seconds apart, and equal-looking packages of
// different closures shared a key. Pure unit test: no engine, one two-second wait.
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

public sealed class InProcessAppPackagerReproducibleTests : IDisposable
{
    private readonly string _root = TestScratch.Dir("al-runner-packager-reproducible");

    public InProcessAppPackagerReproducibleTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void PackagingOneBundleTwiceOnDifferentSeconds_GivesTheSameBytes()
    {
        var bundle = Directory.CreateDirectory(Path.Combine(_root, "bundle")).FullName;
        File.WriteAllText(Path.Combine(bundle, "app.json"),
            """{"id":"7d1c3a52-0b6e-4f3e-9a41-5e2b8c9d5306","name":"packager-repro","publisher":"repro","version":"1.0.0.0","runtime":"14.0"}""");
        File.WriteAllText(Path.Combine(bundle, "Unit.Codeunit.al"), "codeunit 50100 \"Packager Repro\" { }");
        var identity = InProcessAppPackager.ReadIdentity(Path.Combine(bundle, "app.json"))!;

        var first = Path.Combine(_root, "first.app");
        InProcessAppPackager.EmitAppPackageToFile(bundle, identity, first);
        Thread.Sleep(TimeSpan.FromMilliseconds(2100));   // past one DOS-time tick, which is two seconds
        var second = Path.Combine(_root, "second.app");
        InProcessAppPackager.EmitAppPackageToFile(bundle, identity, second);

        Assert.Equal(File.ReadAllBytes(first), File.ReadAllBytes(second));

        // And the bytes still follow the content: a changed source is a different package.
        File.WriteAllText(Path.Combine(bundle, "Unit.Codeunit.al"), "codeunit 50100 \"Packager Repro\" { procedure Added() begin end; }");
        var third = Path.Combine(_root, "third.app");
        InProcessAppPackager.EmitAppPackageToFile(bundle, identity, third);
        Assert.NotEqual(File.ReadAllBytes(first), File.ReadAllBytes(third));
    }
}
