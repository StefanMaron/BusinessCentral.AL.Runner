namespace AlRunner.Runtime;

/// <summary>
/// Stub for <c>ALSystemOperatingSystem</c>. The real type's
/// <c>ALHyperlink</c> reaches into <c>NavSession</c> to dispatch an
/// OS-level URL open and throws <c>NullReferenceException</c> in
/// standalone mode — there's no session or client surface.
///
/// MockSystemOperatingSystem makes Hyperlink a no-op so tests that
/// exercise documentation-linking code paths (a common pattern in
/// AL rules that call <c>Hyperlink(WikiUrl)</c> from
/// <c>ShowMoreDetails</c>) don't crash.
/// </summary>
public static class MockSystemOperatingSystem
{
    public static void ALHyperlink(string hyperlink, System.Guid automationId)
    {
        // No-op: there's no client to open the URL.
    }

    public static void ALHyperlink(string hyperlink)
    {
        // No-op.
    }

    /// <summary>
    /// ALApplicationPath — AL: ApplicationPath() — returns the application's base directory.
    /// In standalone mode, returns the runner's own base directory.
    /// </summary>
    public static string ALApplicationPath
        => System.AppContext.BaseDirectory;

    /// <summary>
    /// ALTemporaryPath — AL: TemporaryPath() — returns the OS temp directory.
    /// </summary>
    public static string ALTemporaryPath
        => System.IO.Path.GetTempPath();

    /// <summary>
    /// ALGuiAllowed — AL: GuiAllowed() — returns whether a GUI client is available.
    /// In standalone mode there is no client surface, so this always returns false.
    /// </summary>
    public static bool ALGuiAllowed => false;

    /// <summary>
    /// GetUrl(ClientType) — 1-arg overload.
    /// </summary>
    public static string ALGetUrl(object clientType)
    {
        return "/mock";
    }

    /// <summary>
    /// GetUrl(ClientType, Company) — 2-arg overload.
    /// </summary>
    public static string ALGetUrl(object clientType, string company)
    {
        return $"/mock?company={company}";
    }

    /// <summary>
    /// GetUrl(ClientType, Company, ObjectType, ObjectId [, Record [, UseFilters]]) — full overload.
    /// </summary>
    public static string ALGetUrl(object clientType, string company, object objectType, int objectId, object? record = null, bool useFilters = false)
    {
        return $"/mock/{objectType}/{objectId}";
    }
}
