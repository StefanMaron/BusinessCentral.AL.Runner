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

    /// <summary>
    /// Resets the session counter between tests.
    /// </summary>
    public static void Reset()
    {
        _nextSessionId = 1;
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
    /// Dispatches the codeunit synchronously and returns true.
    /// The record parameter is accepted but not passed to OnRun (OnRun with record params
    /// is a known limitation of the runner).
    /// </summary>
    public static bool ALStartSession(DataError errorLevel, ByRef<int> sessionId, int codeunitId, string companyName, MockRecordHandle record)
    {
        return ALStartSession(errorLevel, sessionId, codeunitId, companyName);
    }

    /// <summary>
    /// StartSession with record parameter (int sessionId variant — when ByRef is stripped).
    /// </summary>
    public static bool ALStartSession(DataError errorLevel, int sessionId, int codeunitId, string companyName, MockRecordHandle record)
    {
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
        return ALStartSession(errorLevel, sessionId, codeunitId, companyName);
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
    /// Sleep — no-op in standalone mode.
    /// Replaces NavSession.Sleep(int milliseconds).
    /// </summary>
    public static void Sleep(int milliseconds)
    {
        // No-op: don't actually sleep in test mode.
    }
}
