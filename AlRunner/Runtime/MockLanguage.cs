namespace AlRunner.Runtime;

/// <summary>
/// Standalone replacement for BC language APIs (GlobalLanguage).
/// ALSystemLanguage.get_ALGlobalLanguage / set_ALGlobalLanguage crash because
/// there is no live BC session context in standalone mode. This class provides
/// an in-memory static field with a sensible default (1033 = ENU).
/// </summary>
public static class MockLanguage
{
    private static int _globalLanguage = 1033; // ENU default

    /// <summary>
    /// Replaces ALSystemLanguage.ALGlobalLanguage (get/set).
    /// Default is 1033 (English US — ENU), matching the BC default in a fresh environment.
    /// </summary>
    public static int ALGlobalLanguage
    {
        get => _globalLanguage;
        set => _globalLanguage = value;
    }

    /// <summary>
    /// Replaces ALSystemLanguage.ALWindowsLanguage (get).
    /// Returns the same LCID as GlobalLanguage — standalone has a single language context.
    /// </summary>
    public static int ALWindowsLanguage => _globalLanguage;

    private static Microsoft.Dynamics.Nav.Runtime.NavDate? _workDate;

    /// <summary>
    /// Replaces ALSystemDate.ALWorkDate — get (0 args) and set (1 NavDate arg).
    /// Default is Today(); setter overrides.
    /// </summary>
    public static Microsoft.Dynamics.Nav.Runtime.NavDate ALWorkDate
    {
        get
        {
            if (_workDate != null) return _workDate;
            var now = System.DateTime.Today;
            return AlCompat.DMY2Date(now.Day, now.Month, now.Year);
        }
        set => _workDate = value;
    }

    /// <summary>ALWorkDate setter that takes NavDate parameter (matches the BC 1-arg form).</summary>
    public static void SetWorkDate(Microsoft.Dynamics.Nav.Runtime.NavDate d)
    {
        _workDate = d;
    }

    /// <summary>
    /// Resets the language back to the ENU default between tests.
    /// Called by Executor.ResetAll() between test runs.
    /// </summary>
    public static void Reset()
    {
        _globalLanguage = 1033;
        _workDate = default;
    }
}
