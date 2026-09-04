// DepExtractionDir — where a dependency's AL source is extracted for compilation (issue #2696).
//
// This used to be Path.GetTempPath()/al-runner-deps/<Publisher>_<Name>_<Version>: identical for
// every process on the machine. The extraction DELETES the .al files already there before
// rewriting them, so two runners resolving the same dependency at the same moment raced — one
// deleted the files the other was compiling from.
//
// Measured under `--jobs 6` on Microsoft's BaseApp buckets: Tests-TestLibraries is a dependency
// of nearly every bucket, so all six workers extracted it at once. One died with
// `FileNotFoundException: .../BackupManagement.Codeunit.al` before starting ANY of its bundles,
// removing Tests-SCM (8,526 tests), Tests-Cost Accounting and Tests-Monitor Sensitive Fields
// from a run that still printed a confident aggregate.
//
// Not specific to --jobs: two terminals, a --watch session beside a CLI run, or two CI jobs on
// one self-hosted runner share a TMPDIR too. --jobs only makes it near-certain.
//
// Isolating per process costs nothing, which is why this is the fix rather than locking or an
// atomic publish. The real cache is the compiled DLL (DependencyLoader returns on a
// source-cache HIT before reaching here), so this directory is scratch space that is fully
// rewritten on every miss — there was never any cross-process reuse to preserve.
//
// The per-process root stays UNDER the shared al-runner-deps parent so anything that cleans
// that parent still finds it, and it is removed on normal process exit. A killed process leaves
// its root behind, exactly as the previous shared directory was left behind forever.

namespace AlRunner.Infrastructure;

internal static class DepExtractionDir
{
    /// <summary>
    /// The root a process with this id would use. Exposed so a test can prove two processes get
    /// DIFFERENT roots — asserting only that the live path contains the current pid would still
    /// pass if the pid were, say, appended to a shared constant in a way that collided.
    /// </summary>
    public static string RootForProcess(int processId)
        => Path.Combine(Path.GetTempPath(), "al-runner-deps", $"p{processId}");

    private static readonly Lazy<string> _root = new(() =>
    {
        var root = RootForProcess(Environment.ProcessId);
        Directory.CreateDirectory(root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        };
        return root;
    });

    /// <summary>This process's extraction root, under the shared al-runner-deps parent.</summary>
    public static string Root => _root.Value;

    /// <summary>
    /// The directory for one dependency. Stable within a process — a dependency resolved twice
    /// (ordinary under --watch) reuses it rather than leaking a directory per call — and never
    /// shared with another process.
    /// </summary>
    public static string For(string publisher, string name, string version)
    {
        var leaf = Sanitize($"{publisher}_{name}_{version}");
        return Path.Combine(Root, leaf);
    }

    /// <summary>
    /// Reduce a manifest-supplied identity to one safe path segment. A '/' or '..' from a
    /// third-party .app must land inside the root, not above it.
    /// </summary>
    private static string Sanitize(string s)
    {
        var chars = s.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(Path.GetInvalidFileNameChars(), chars[i]) >= 0
                || chars[i] == '/' || chars[i] == '\\' || chars[i] == '.')
                chars[i] = '_';
        var leaf = new string(chars).Trim('_');
        return leaf.Length == 0 ? "dep" : leaf;
    }
}
