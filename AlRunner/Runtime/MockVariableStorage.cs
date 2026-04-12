using System;
using System.Collections.Generic;

namespace AlRunner.Runtime;

/// <summary>
/// Mock implementation of BC's "Library - Variable Storage" (codeunit 131004).
/// Provides an in-memory FIFO queue for passing values between test setup and handler functions.
/// </summary>
public static class MockVariableStorage
{
    private static readonly Queue<object?> _queue = new();

    public static void Enqueue(object? value)
    {
        _queue.Enqueue(value);
    }

    public static object? Dequeue()
    {
        if (_queue.Count == 0)
            throw new Exception("Queue is empty");
        return _queue.Dequeue();
    }

    public static void AssertEmpty()
    {
        if (_queue.Count > 0)
            throw new Exception($"Queue is not empty. {_queue.Count} item(s) remaining.");
    }

    public static void Clear()
    {
        _queue.Clear();
    }

    public static bool IsEmpty()
    {
        return _queue.Count == 0;
    }

    /// <summary>Reset between tests.</summary>
    public static void Reset()
    {
        _queue.Clear();
    }
}
