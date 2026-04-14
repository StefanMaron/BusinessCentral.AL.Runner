namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// Standalone replacement for ALTaskScheduler static methods.
/// ALTaskScheduler.ALCreateTask/ALTaskExists/ALCancelTask/ALSetTaskReady require
/// the BC service tier. CreateTask dispatches the codeunit synchronously via
/// MockCodeunitHandle (same pattern as MockSession.ALStartSession).
/// </summary>
public static class MockTaskScheduler
{
    /// <summary>
    /// CreateTask — dispatches the target codeunit synchronously and returns a new Guid.
    /// Matches ALTaskScheduler.ALCreateTask(int codeunitId, int failureCodeunitId, bool isReady).
    /// </summary>
    public static Guid ALCreateTask(int codeunitId, int failureCodeunitId, bool isReady)
    {
        var taskId = Guid.NewGuid();
        try
        {
            MockCodeunitHandle.RunCodeunit(codeunitId);
        }
        catch
        {
            // Swallow: in BC, task failures are logged, not thrown
        }
        return taskId;
    }

    /// <summary>
    /// CreateTask with company name parameter.
    /// </summary>
    public static Guid ALCreateTask(int codeunitId, int failureCodeunitId, bool isReady, string companyName)
    {
        return ALCreateTask(codeunitId, failureCodeunitId, isReady);
    }

    /// <summary>
    /// CreateTask with company name and NotBefore datetime.
    /// </summary>
    public static Guid ALCreateTask(int codeunitId, int failureCodeunitId, bool isReady, string companyName, NavDateTime notBefore)
    {
        return ALCreateTask(codeunitId, failureCodeunitId, isReady);
    }

    /// <summary>
    /// CreateTask with company name, NotBefore, and RecordId.
    /// </summary>
    public static Guid ALCreateTask(int codeunitId, int failureCodeunitId, bool isReady, string companyName, NavDateTime notBefore, NavRecordId recordId)
    {
        return ALCreateTask(codeunitId, failureCodeunitId, isReady);
    }

    /// <summary>
    /// TaskExists — always returns false (task completed synchronously).
    /// </summary>
    public static bool ALTaskExists(Guid taskId)
    {
        return false;
    }

    /// <summary>
    /// CancelTask — no-op (task already completed synchronously).
    /// </summary>
    public static bool ALCancelTask(Guid taskId)
    {
        return true;
    }

    /// <summary>
    /// SetTaskReady — no-op (task already ran synchronously).
    /// </summary>
    public static bool ALSetTaskReady(Guid taskId)
    {
        return true;
    }

    /// <summary>
    /// SetTaskReady with NotBefore parameter.
    /// </summary>
    public static bool ALSetTaskReady(Guid taskId, NavDateTime notBefore)
    {
        return true;
    }
}
