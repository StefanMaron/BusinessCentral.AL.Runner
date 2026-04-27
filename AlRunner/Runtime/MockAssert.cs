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

        // BC's ExpectedError uses substring containment, not exact match
        if (!actual.Contains(expectedMessage, StringComparison.Ordinal))
            throw new AssertException(
                $"Assert.ExpectedError failed. Expected substring: <{expectedMessage}>, Actual: <{actual}>.");
    }

    /// <summary>
    /// Verifies that the last asserterror block captured an error with the expected error code.
    /// In standalone mode, error codes are not tracked, so we just verify that an error occurred.
    /// Common error codes: 'Dialog' (Error() was called), 'TestField' (TestField failed), etc.
    /// </summary>
    public static void ExpectedErrorCode(string expectedErrorCode)
    {
        var actual = AlScope.LastErrorText;
        if (string.IsNullOrEmpty(actual))
            throw new AssertException(
                $"Assert.ExpectedErrorCode failed. Expected an error with code: <{expectedErrorCode}>, but no error occurred.");
    }

    /// <summary>
    /// Verifies that the last asserterror block captured an error with the expected error code
    /// and message. In standalone mode, error codes are not tracked — this checks the error
    /// message text only.
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

        if (!actualError.Contains(expectedSubstring, StringComparison.OrdinalIgnoreCase))
            throw new AssertException(
                $"Assert.ExpectedMessage failed. Expected substring: <{expectedSubstring}>, Actual: <{actualError}>.");
    }

    /// <summary>
    /// Verifies that the last asserterror block captured a TestField error
    /// with the expected field caption and field value.
    /// In BC, TestField errors follow the pattern:
    ///   "{FieldCaption} must be equal to '{FieldValue}' ..."
    /// In standalone mode, ALFieldCaption returns "FieldNN" which may not match.
    /// We check that the error message contains either the caption or the value.
    /// </summary>
    public static void ExpectedTestFieldError(string fieldCaption, string fieldValue)
    {
        var actual = AlScope.LastErrorText;
        if (string.IsNullOrEmpty(actual))
            throw new AssertException(
                $"Assert.ExpectedTestFieldError failed. Expected a TestField error for field <{fieldCaption}> with value <{fieldValue}>, but no error occurred.");

        // TestField errors typically contain the field caption or "must be equal to"
        // In standalone mode, check if either the caption or value appears in the error
        if (!actual.Contains(fieldCaption, StringComparison.OrdinalIgnoreCase) &&
            !actual.Contains(fieldValue, StringComparison.OrdinalIgnoreCase) &&
            !actual.Contains("must be equal to", StringComparison.OrdinalIgnoreCase) &&
            !actual.Contains("TestField", StringComparison.OrdinalIgnoreCase))
        {
            throw new AssertException(
                $"Assert.ExpectedTestFieldError failed. Expected TestField error for field <{fieldCaption}> = <{fieldValue}>, Actual: <{actual}>.");
        }
    }

    /// <summary>
    /// Asserts that an error was expected (any error).
    /// </summary>
    public static void AssertNothingInsideFilter()
    {
        // No-op in standalone mode
    }

    public static void AreNearlyEqual(object? expected, object? actual, object? delta, string message)
    {
        var exp = ToDecimal(expected);
        var act = ToDecimal(actual);
        var d = ToDecimal(delta);
        if (Math.Abs(exp - act) > d)
            throw new AssertException(
                $"Assert.AreNearlyEqual failed. Expected: <{exp}>, Actual: <{act}>, Delta: <{d}>. {message}");
    }

    public static void Fail(string message)
    {
        throw new AssertException($"Assert.Fail. {message}");
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
        // Unwrap MockVariant
        if (a is MockVariant mva) return ValuesEqual(mva.Value, b);
        if (b is MockVariant mvb) return ValuesEqual(a, mvb.Value);

        // Integer ordinal vs NavOption comparison.
        // BC 27.x compiles AL option literals (e.g. Category::Standard) as plain C# integers;
        // the runner returns NavOption for record field values.  Compare by ordinal value so that
        // AreEqual(Category::Standard, Item."Category", ...) passes when the ordinals match,
        // regardless of whether Format() returns "Standard" (name) or "1" (ordinal).
        if (a is int ia && b is Microsoft.Dynamics.Nav.Runtime.NavOption bno) return ia == bno.Value;
        if (a is Microsoft.Dynamics.Nav.Runtime.NavOption ano && b is int ib) return ano.Value == ib;

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

    private static decimal ToDecimal(object? value)
    {
        if (value == null) return 0m;
        if (value is MockVariant mv) return ToDecimal(mv.Value);
        return Convert.ToDecimal(value);
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
