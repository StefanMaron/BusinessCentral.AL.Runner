// LoadedAssemblyReferences — the one place a test turns "every assembly loaded in this process"
// into Roslyn references (#5414). Test classes that compile a stand-in type against the live BC
// engine need the loaded assemblies, and `Assembly.Location` of one of them can name a file that
// no longer exists: an assembly stays in AppDomain.GetAssemblies() after whatever directory it
// was loaded from has been deleted, and on Linux the mapped image keeps working.

using System.Reflection;
using Microsoft.CodeAnalysis;

namespace AlRunner.Tests;

internal static class LoadedAssemblyReferences
{
    /// <summary>
    /// References for <paramref name="assemblies"/> (default: the whole AppDomain), in order.
    /// An assembly with no file behind its Location is skipped rather than throwing out of
    /// CreateFromFile. Skipping cannot hide a reference a compile needs: a type that lived only
    /// in the skipped assembly fails the compile with its own CS0246 naming that type, which the
    /// callers assert on.
    /// </summary>
    public static List<MetadataReference> Build(IEnumerable<Assembly>? assemblies = null)
        => (assemblies ?? AppDomain.CurrentDomain.GetAssemblies())
            .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && File.Exists(a.Location))
            .Select(a => (MetadataReference)MetadataReference.CreateFromFile(a.Location))
            .ToList();
}
