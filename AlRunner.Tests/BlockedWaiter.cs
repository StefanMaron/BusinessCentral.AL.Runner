// BlockedWaiter — starts work on a pool thread and returns only once that thread is blocked in
// CacheCompileLock.Acquire's poll sleep, so a test can release the holder knowing the work met it
// (#5276).
//
// Why not "a task that has not finished after N ms": that proves only that the task has not
// finished. A pool thread that is slow to start the task (a loaded CI leg) lets the holder go
// first, the work finds the lock free and does not wait, and the assertion on "it waited" fails
// with nothing wrong in the lock. The state below is what a lock waiter is in while it waits.
//
// What it still cannot prove: WaitSleepJoin is any blocking wait, not only Acquire's
// Thread.Sleep(PollEvery). Nothing between the signal and the first open attempt blocks, so the
// first WaitSleepJoin is that sleep, which Acquire reaches only after an open found the lock held.
// Work that sleeps or takes a monitor before it reaches Acquire would satisfy it wrongly.

using Xunit.Sdk;

namespace AlRunner.Tests;

internal static class BlockedWaiter
{
    /// <summary>Runs <paramref name="work"/> on a pool thread and returns once that thread has
    /// started it and is blocked. Throws when the work finishes without ever blocking (it did not
    /// wait for the holder) or when the thread never starts or blocks within
    /// <paramref name="timeout"/>.</summary>
    public static Task<T> StartUntilBlocked<T>(Func<T> work, TimeSpan timeout)
    {
        Thread? worker = null;
        var started = new ManualResetEventSlim();
        var task = Task.Run(() =>
        {
            worker = Thread.CurrentThread;
            started.Set();
            return work();
        });

        var deadline = DateTime.UtcNow + timeout;
        if (!started.Wait(timeout))
            throw new XunitException($"the pool thread did not start the work within {timeout}");
        while (DateTime.UtcNow < deadline)
        {
            if (task.IsCompleted)
                throw new XunitException("the work finished without waiting: it went on while the holder still held the lock");
            if ((worker!.ThreadState & ThreadState.WaitSleepJoin) != 0) return task;
            Thread.Sleep(5);
        }
        throw new XunitException($"the work started but was not blocked within {timeout}");
    }
}
