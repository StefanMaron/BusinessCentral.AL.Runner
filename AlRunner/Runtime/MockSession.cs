namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Standalone replacement for BC session management APIs (StartSession, StopSession,
/// IsSessionActive, Sleep). StartSession dispatches the target codeunit synchronously
/// via MockCodeunitHandle — same pattern as Codeunit.Run. This gives tests real results
/// even though no background session is spawned.
/// </summary>
public static class MockSession
{
    private static int _nextSessionId = 1;
    private static string _companyName = "CRONUS";
    private static string _companyDisplayName = "My Company";
    private static string _companyUrlName = "My%20Company";
    private static Guid _companyId = new Guid("5f5f5f5f-5f5f-5f5f-5f5f-5f5f5f5f5f5f");

    /// <summary>
    /// Default company name applied between tests (settable via the
    /// <c>--company-name</c> CLI flag). Defaults to "CRONUS" so AL code calling
    /// CompanyName() gets a stable non-empty value without any CLI flag.
    /// </summary>
    public static string DefaultCompanyName { get; set; } = "CRONUS";

    /// <summary>Default DisplayName returned by CompanyProperty.DisplayName().</summary>
    public static string DefaultCompanyDisplayName { get; set; } = "My Company";

    /// <summary>Default UrlName returned by CompanyProperty.UrlName().</summary>
    public static string DefaultCompanyUrlName { get; set; } = "My%20Company";

    /// <summary>Default ID returned by CompanyProperty.ID().</summary>
    public static Guid DefaultCompanyId { get; set; } = new Guid("5f5f5f5f-5f5f-5f5f-5f5f-5f5f5f5f5f5f");

    /// <summary>Value returned by the AL <c>CompanyName()</c> built-in.</summary>
    public static string GetCompanyName() => _companyName;

    /// <summary>Value returned by <c>CompanyProperty.DisplayName()</c>.</summary>
    public static string GetCompanyDisplayName() => _companyDisplayName;

    /// <summary>Value returned by <c>CompanyProperty.UrlName()</c>.</summary>
    public static string GetCompanyUrlName() => _companyUrlName;

    /// <summary>Value returned by <c>CompanyProperty.ID()</c>.</summary>
    public static Guid GetCompanyId() => _companyId;

    /// <summary>
    /// Value returned by the AL <c>SessionId()</c> built-in.
    /// Returns a stable non-zero integer (1) — the exact value is not meaningful
    /// in standalone mode; the only contract is that it is positive and consistent.
    /// </summary>
    public static int GetSessionId() => 1;

    /// <summary>
    /// Sets the value returned by the AL <c>CompanyName()</c> built-in.
    /// Reset back to <see cref="DefaultCompanyName"/> before each test.
    /// </summary>
    public static void SetCompanyName(string name) => _companyName = name ?? string.Empty;

    /// <summary>Sets the value returned by <c>CompanyProperty.DisplayName()</c>.</summary>
    public static void SetCompanyDisplayName(string name) => _companyDisplayName = name ?? string.Empty;

    /// <summary>Sets the value returned by <c>CompanyProperty.UrlName()</c>.</summary>
    public static void SetCompanyUrlName(string name) => _companyUrlName = name ?? string.Empty;

    /// <summary>Sets the GUID returned by <c>CompanyProperty.ID()</c>.</summary>
    public static void SetCompanyId(Guid id) => _companyId = id;

    /// <summary>
    /// Resets the session counter and company name between tests.
    /// </summary>
    public static void Reset()
    {
        _nextSessionId = 1;
        _companyName = DefaultCompanyName;
        _companyDisplayName = DefaultCompanyDisplayName;
        _companyUrlName = DefaultCompanyUrlName;
        _companyId = DefaultCompanyId;
    }

    /// <summary>
    /// StartSession without company parameter (2-arg AL form).
    /// ALSession.ALStartSession(DataError, ByRef&lt;int&gt; sessionId, int codeunitId)
    /// Equivalent to calling the 3-arg form with the current company name.
    /// </summary>
    public static bool ALStartSession(DataError errorLevel, ByRef<int> sessionId, int codeunitId)
    {
        return ALStartSession(errorLevel, sessionId, codeunitId, _companyName);
    }

    /// <summary>
    /// StartSession without record parameter.
    /// ALSession.ALStartSession(DataError, ByRef&lt;int&gt; sessionId, int codeunitId, string companyName)
    /// Dispatches the codeunit synchronously and returns true.
    /// </summary>
    public static bool ALStartSession(DataError errorLevel, ByRef<int> sessionId, int codeunitId, string companyName)
    {
        sessionId.Value = _nextSessionId++;
        try
        {
            MockCodeunitHandle.RunCodeunit(codeunitId);
            return true;
        }
        catch
        {
            if (errorLevel == DataError.TrapError)
                return false;
            throw;
        }
    }

    /// <summary>
    /// StartSession with record parameter.
    /// ALSession.ALStartSession(DataError, ByRef&lt;int&gt; sessionId, int codeunitId, string companyName, MockRecordHandle record)
    /// Dispatches the codeunit synchronously and forwards the record to OnRun.
    /// </summary>
    public static bool ALStartSession(DataError errorLevel, ByRef<int> sessionId, int codeunitId, string companyName, MockRecordHandle record)
    {
        sessionId.Value = _nextSessionId++;
        // RunCodeunit already handles TrapError (returns false) and ThrowError (throws).
        // Return its result directly so a trapped failure is propagated as false, not true.
        return MockCodeunitHandle.RunCodeunit(errorLevel, codeunitId, record);
    }

    /// <summary>
    /// StartSession with record parameter (int sessionId variant — when ByRef is stripped).
    /// </summary>
    public static bool ALStartSession(DataError errorLevel, int sessionId, int codeunitId, string companyName, MockRecordHandle record)
    {
        return MockCodeunitHandle.RunCodeunit(errorLevel, codeunitId, record);
    }

    /// <summary>
    /// StartSession with NavDuration timeout (5-arg with timeout, no record).
    /// </summary>
    public static bool ALStartSession(DataError errorLevel, ByRef<int> sessionId, int codeunitId, string companyName, NavDuration timeout)
    {
        return ALStartSession(errorLevel, sessionId, codeunitId, companyName);
    }

    /// <summary>
    /// StartSession with record + timeout (6-arg).
    /// </summary>
    public static bool ALStartSession(DataError errorLevel, ByRef<int> sessionId, int codeunitId, string companyName, MockRecordHandle record, NavDuration timeout)
    {
        return ALStartSession(errorLevel, sessionId, codeunitId, companyName, record);
    }

    /// <summary>
    /// IsSessionActive — always returns false (session completed synchronously).
    /// The first argument is the BC session object (rewritten to null!), which we ignore.
    /// </summary>
    public static bool ALIsSessionActive(object? session, int sessionId)
    {
        return false;
    }

    /// <summary>
    /// StopSession — no-op (session already completed synchronously).
    /// </summary>
    public static void ALStopSession(DataError errorLevel, int sessionId)
    {
        // No-op: session already completed synchronously.
    }

    /// <summary>
    /// StopSession with comment — no-op (session already completed synchronously).
    /// Maps to the AL <c>StopSession(SessionId, Comment)</c> 2-arg overload.
    /// The comment parameter is accepted but ignored in standalone mode.
    /// </summary>
    public static void ALStopSession(DataError errorLevel, int sessionId, string comment)
    {
        // No-op: session already completed synchronously; comment is ignored.
    }

    /// <summary>
    /// Sleep — no-op in standalone mode.
    /// Replaces NavSession.Sleep(int milliseconds).
    /// </summary>
    public static void Sleep(int milliseconds)
    {
        // No-op: don't actually sleep in test mode.
    }
}
