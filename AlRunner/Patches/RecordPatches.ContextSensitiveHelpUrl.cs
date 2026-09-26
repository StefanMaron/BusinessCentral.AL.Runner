using System.Xml.Linq;

namespace AlRunner.Patches;

// HelpLink on the metadata the runner synthesizes for a precompiled dependency's pages, report
// request pages and queries (#4675). See docs/dependency-page-properties.md#helplink.
public static partial class RecordPatches
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?>
        _appContextSensitiveHelpUrl = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The HelpLink BC's compiler writes for an object whose app manifest states
    /// <paramref name="manifestHelpUrl"/>, or null when it writes none.
    ///
    /// <para><b>Observably equivalent:</b> a stated <c>HelpLink</c> is written verbatim; otherwise
    /// this is <c>PageMetadataEmitHelper.GetContextSensitiveHelpUrl</c> in
    /// Microsoft.Dynamics.Nav.CodeAnalysis.dll — null when the manifest states no
    /// <c>ContextSensitiveHelpUrl</c>, the URL alone when no <c>ContextSensitiveHelpPage</c> is
    /// stated, else URL + page with no separator. Page, request-page and query emitters all call
    /// it. Corpus codeunit 67250 pins the no-URL arm on a Base Application report.</para>
    /// </summary>
    internal static string? DeriveHelpLink(
        string? statedHelpLink, string? contextSensitiveHelpPage, string? manifestHelpUrl)
    {
        if (!string.IsNullOrEmpty(statedHelpLink)) return statedHelpLink;
        if (string.IsNullOrEmpty(manifestHelpUrl)) return null;
        return manifestHelpUrl + (contextSensitiveHelpPage ?? string.Empty);
    }

    /// <summary>
    /// <c>App/@ContextSensitiveHelpUrl</c> from the <c>.app</c>'s own NavxManifest.xml, or null
    /// when the manifest states none. A package carrying no manifest states nothing either.
    /// A manifest that exists but cannot be parsed throws: answering null there would drop a
    /// HelpLink the app declares.
    /// </summary>
    internal static string? DependencyAppContextSensitiveHelpUrl(string appPath)
        => _appContextSensitiveHelpUrl.GetOrAdd(appPath, ReadAppContextSensitiveHelpUrl);

    private static string? ReadAppContextSensitiveHelpUrl(string appPath)
    {
        var xml = AppLoader.ReadNavxManifestXml(appPath);
        if (string.IsNullOrEmpty(xml)) return null;
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException ex)
        {
            throw new InvalidDataException(
                $"NavxManifest.xml of '{appPath}' does not parse, so its ContextSensitiveHelpUrl "
                + $"(the base of every HelpLink the app's pages carry) cannot be read: {ex.Message}", ex);
        }
        var app = doc.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "App");
        var url = app?.Attribute("ContextSensitiveHelpUrl")?.Value;
        return string.IsNullOrEmpty(url) ? null : url;
    }

    private static void ClearAppContextSensitiveHelpUrls() => _appContextSensitiveHelpUrl.Clear();
}
