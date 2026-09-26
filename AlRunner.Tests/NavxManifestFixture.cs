using System.IO.Compression;
using System.Security;
using System.Text;

namespace AlRunner.Tests;

/// <summary>
/// Adds a minimal NavxManifest.xml to a fixture .app. A dependency's HelpLink is derived from its
/// manifest's ContextSensitiveHelpUrl (#4675), so a fixture that asserts a HelpLink has to state one.
/// </summary>
internal static class NavxManifestFixture
{
    internal static void Add(ZipArchive za, string contextSensitiveHelpUrl)
    {
        var entry = za.CreateEntry("NavxManifest.xml");
        using var w = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        w.Write(
            "<Package xmlns=\"http://schemas.microsoft.com/navx/2015/manifest\">"
            + $"<App Id=\"{Guid.NewGuid()}\" Name=\"HelpLink Fixture\" Publisher=\"Test\" Version=\"1.0.0.0\""
            + $" ContextSensitiveHelpUrl=\"{SecurityElement.Escape(contextSensitiveHelpUrl)}\" />"
            + "</Package>");
    }
}
