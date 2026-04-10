namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;

/// <summary>
/// Mock implementation of BC's "Library Assert" codeunit (ID 130).
/// Provides assertion methods used by BC test codeunits.
/// Wired into MockCodeunitHandle so that calls to codeunit 130 route here.
/// </summary>
public static class MockAssert
{
    public static void AreEqual(object? expected, object? actual, string message)
    {
        var expectedStr = FormatValue(expected);
        var actualStr = FormatValue(actual);
        if (!ValuesEqual(expected, actual))
            throw new AssertException(
                $"Assert.AreEqual failed. Expected: <{expectedStr}>, Actual: <{actualStr}>. {message}");
    }

    public static void AreNotEqual(object? expected, object? actual, string message)
    {
        if (ValuesEqual(expected, actual))
        {
            var valueStr = FormatValue(expected);
            throw new AssertException(
                $"Assert.AreNotEqual failed. Expected any value except: <{valueStr}>. {message}");
        }
    }

    public static void IsTrue(object? condition, string message)
    {
        bool value = ToBool(condition);
        if (!value)
            throw new AssertException($"Assert.IsTrue failed. {message}");
    }

    public static void IsFalse(object? condition, string message)
    {
        bool value = ToBool(condition);
        if (value)
            throw new AssertException($"Assert.IsFalse failed. {message}");
    }

    /// <summary>
    /// Verifies that the last asserterror block captured the expected error message.
    /// </summary>
    public static void ExpectedError(string expectedMessage)
    {
        var actual = AlScope.LastErrorText;
        if (string.IsNullOrEmpty(actual))
            throw new AssertException(
                $"Assert.ExpectedError failed. Expected an error with message: <{expectedMessage}>, but no error occurred.");

        if (!string.Equals(actual, expectedMessage, StringComparison.Ordinal))
            throw new AssertException(
                $"Assert.ExpectedError failed. Expected: <{expectedMessage}>, Actual: <{actual}>.");
    }

    /// <summary>
    /// Verifies that the last asserterror block captured an error with the expected error code.
    /// In standalone mode, error codes are not tracked — this checks the error message text.
    /// </summary>
    public static void ExpectedErrorCode(string expectedErrorCode, string expectedMessage)
    {
        // Error codes are not tracked in standalone mode; verify message instead
        ExpectedError(expectedMessage);
    }

    /// <summary>
    /// Verifies that an expected error message is a substring of the actual error.
    /// BC's ExpectedMessage checks for substring containment.
    /// </summary>
    public static void ExpectedMessage(string expectedSubstring, string actualError)
    {
        if (string.IsNullOrEmpty(actualError))
            actualError = AlScope.LastErrorText;

        if (!actualError.Contains(expectedSubstring, StringComparison.Ordinal))
            throw new AssertException(
                $"Assert.ExpectedMessage failed. Expected substring: <{expectedSubstring}>, Actual: <{actualError}>.");
    }

    /// <summary>
    /// Asserts that an error was expected (any error).
    /// </summary>
    public static void AssertNothingInsideFilter()
    {
        // No-op in standalone mode
    }

    public static void RecordIsEmpty(MockRecordHandle record)
    {
        if (!record.ALIsEmpty)
            throw new AssertException("Assert.RecordIsEmpty failed. The record set is not empty.");
    }

    public static void RecordIsNotEmpty(MockRecordHandle record)
    {
        if (record.ALIsEmpty)
            throw new AssertException("Assert.RecordIsNotEmpty failed. The record set is empty.");
    }

    public static void RecordCount(MockRecordHandle record, int expectedCount)
    {
        var actual = record.ALCount;
        if (actual != expectedCount)
            throw new AssertException(
                $"Assert.RecordCount failed. Expected: <{expectedCount}>, Actual: <{actual}>.");
    }

    public static void TableIsEmpty(int tableId)
    {
        var rec = new MockRecordHandle(tableId);
        if (!rec.ALIsEmpty)
            throw new AssertException($"Assert.TableIsEmpty failed. Table {tableId} is not empty.");
    }

    public static void TableIsNotEmpty(int tableId)
    {
        var rec = new MockRecordHandle(tableId);
        if (rec.ALIsEmpty)
            throw new AssertException($"Assert.TableIsNotEmpty failed. Table {tableId} is empty.");
    }

    // --- Internal helpers ---

    private static bool ValuesEqual(object? a, object? b)
    {
        var aStr = FormatValue(a);
        var bStr = FormatValue(b);
        return string.Equals(aStr, bStr, StringComparison.Ordinal);
    }

    private static string FormatValue(object? value)
    {
        if (value == null) return "";
        if (value is MockVariant mv) return FormatValue(mv.Value);
        if (value is NavValue nv) return AlCompat.Format(nv);
        return AlCompat.Format(value);
    }

    private static bool ToBool(object? value)
    {
        if (value == null) return false;
        if (value is bool b) return b;
        if (value is MockVariant mv) return ToBool(mv.Value);
        if (value is NavBoolean nb) return (bool)nb;
        return Convert.ToBoolean(value);
    }
}

/// <summary>
/// Exception type for assertion failures. Allows the executor to distinguish
/// assertion failures from other exceptions for error output formatting.
/// </summary>
public class AssertException : Exception
{
    public AssertException(string message) : base(message) { }
}
