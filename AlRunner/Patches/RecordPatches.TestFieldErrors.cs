// RecordPatches.TestFieldErrors — the sync underbelly of Rec.TestField(...): the two
// NavTestFieldException shapes and the field/table names their messages carry.
using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;
public static partial class RecordPatches
{
    // ------------------------------------------------------------------
    // NavRecord.TestFieldNotBlank / TestFieldEquals / TestFieldError
    // ------------------------------------------------------------------
    //
    // These three methods are the sync underbelly of `Rec.TestField(...)`.
    // The real implementations format their failure message using:
    //   - `base.Session.WindowsCulture`
    //   - `ALFieldCaptionAsync(...).AsTask().GetAwaiter().GetResult()`
    //   - `metaField.Parent.TableCaptionSafe`
    //   - `PrimaryKeyString` (iterates key fields)
    //   - `TryAddTestFieldAction(metaField)` (touches `Session.Diagnostics`,
    //      `Session.Permissions`, `NavGlobal.NCLMetadata.GetMetaFormById` —
    //      none of which the skeleton runtime initializes)
    //
    // Empirically (verified 2026-05-09 with Debug-mode emit) the throw path
    // raises a NullReferenceException somewhere inside that argument list,
    // which then surfaces with error code "NullReference" instead of
    // "TestField". Assert.ExpectedTestFieldError sees the wrong code, calls
    // its own Error path, and the test fails with NRE 12 times.
    //
    // Per HANDOFF §5.2 (Option C) we replace the throw path with a clean
    // `NavTestFieldException.CreateNonblank/CreateMustBeEqualTo` call using
    // safe arguments — same factory the real BC code uses, just with culture
    // forced to InvariantCulture and PrimaryKeyValues="" to avoid the
    // failing-skeleton property dives. The exception type is identical
    // (`NavTestFieldException`) so `GetErrorCode` returns "TestField" and
    // Assert.ExpectedTestFieldError's `LastErrorCode.Contains("TestField")`
    // matches. The message contains the field caption, satisfying
    // ExpectedTestFieldMessage's StrPos check.
    //
    // The pass path (value is non-blank / equal) is left to the real code:
    // we delegate to it via reflection and only intercept the throw path.

    private static MethodInfo? _mGetFieldValue;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo?> _pIsZeroOrEmptyByType = new();
    private static MethodInfo? _mNavTestFieldException_CreateNonblank;
    private static MethodInfo? _mNavTestFieldException_CreateMustBeEqualTo;
    private static PropertyInfo? _pNCLMetaFieldFieldNo;
    private static PropertyInfo? _pNCLMetaFieldFieldName;
    private static PropertyInfo? _pNCLMetaFieldParent;
    private static PropertyInfo? _pNCLMetaTableTableName;

    private static string SafeFieldName(object? metaField)
    {
        if (metaField == null) return string.Empty;
        if (_pNCLMetaFieldFieldName == null)
            _pNCLMetaFieldFieldName = metaField.GetType().GetProperty("FieldName",
                BindingFlags.Public | BindingFlags.Instance);
        return (string?)_pNCLMetaFieldFieldName?.GetValue(metaField) ?? string.Empty;
    }

    private static string SafeTableName(object? metaField)
    {
        if (metaField == null) return string.Empty;
        if (_pNCLMetaFieldParent == null)
            _pNCLMetaFieldParent = metaField.GetType().GetProperty("Parent",
                BindingFlags.Public | BindingFlags.Instance);
        var parent = _pNCLMetaFieldParent?.GetValue(metaField);
        if (parent == null) return string.Empty;
        if (_pNCLMetaTableTableName == null)
            _pNCLMetaTableTableName = parent.GetType().GetProperty("TableName",
                BindingFlags.Public | BindingFlags.Instance);
        return (string?)_pNCLMetaTableTableName?.GetValue(parent) ?? string.Empty;
    }

    private static object CreateNavTestFieldException_Nonblank(object metaField)
    {
        if (_mNavTestFieldException_CreateNonblank == null)
        {
            var navTypes = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
            var t = navTypes?.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavTestFieldException")
                ?? navTypes?.GetType("Microsoft.Dynamics.Nav.Types.NavTestFieldException")
                ?? navTypes?.GetTypes().FirstOrDefault(x => x.Name == "NavTestFieldException");
            _mNavTestFieldException_CreateNonblank = t?.GetMethod("CreateNonblank",
                BindingFlags.Public | BindingFlags.Static);
        }
        var m = _mNavTestFieldException_CreateNonblank
            ?? throw new InvalidOperationException("NavTestFieldException.CreateNonblank not found");
        // Signature: (CultureInfo, string fieldName, string tableName, string primaryKeyValues, ErrorInfoData=null)
        var args = new object?[] { System.Globalization.CultureInfo.InvariantCulture,
            SafeFieldName(metaField), SafeTableName(metaField), string.Empty, null };
        return (Exception)m.Invoke(null, args)!;
    }

    private static object CreateNavTestFieldException_MustBeEqualTo(object metaField, string shouldBe, string current)
    {
        if (_mNavTestFieldException_CreateMustBeEqualTo == null)
        {
            var navTypes = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "Microsoft.Dynamics.Nav.Types");
            var t = navTypes?.GetType("Microsoft.Dynamics.Nav.Types.Exceptions.NavTestFieldException")
                ?? navTypes?.GetType("Microsoft.Dynamics.Nav.Types.NavTestFieldException")
                ?? navTypes?.GetTypes().FirstOrDefault(x => x.Name == "NavTestFieldException");
            _mNavTestFieldException_CreateMustBeEqualTo = t?.GetMethod("CreateMustBeEqualTo",
                BindingFlags.Public | BindingFlags.Static);
        }
        var m = _mNavTestFieldException_CreateMustBeEqualTo
            ?? throw new InvalidOperationException("NavTestFieldException.CreateMustBeEqualTo not found");
        // Signature: (CultureInfo, string fieldName, string tableName, string shouldBeValue,
        //            string currentValue, string primaryKeyValues, ErrorInfoData=null)
        var args = new object?[] { System.Globalization.CultureInfo.InvariantCulture,
            SafeFieldName(metaField), SafeTableName(metaField), shouldBe, current, string.Empty, null };
        return (Exception)m.Invoke(null, args)!;
    }

    private static bool TryGetFieldValueIsZeroOrEmpty(object navRecord, object metaField, out bool result)
    {
        result = true;
        try
        {
            // Look up by the NCLMetaField base parameter type (the runtime metaField may be
            // a derived subclass that the public method-resolution can't match directly).
            if (_mGetFieldValue == null)
            {
                var nclMetaFieldT = metaField.GetType();
                while (nclMetaFieldT != null && nclMetaFieldT.Name != "NCLMetaField")
                    nclMetaFieldT = nclMetaFieldT.BaseType;
                _mGetFieldValue = navRecord.GetType().GetMethod("GetFieldValue",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { nclMetaFieldT ?? metaField.GetType() }, null);
            }
            var v = _mGetFieldValue?.Invoke(navRecord, new object[] { metaField });
            if (v == null) { result = true; return true; }
            // NavValue.IsZeroOrEmpty is virtual; cache the PropertyInfo per concrete subtype
            // because the NCLMetaField argument can be different NavValue subclasses across
            // calls (NavInteger / NavText / NavCode / …).
            var p = _pIsZeroOrEmptyByType.GetOrAdd(v.GetType(),
                t => t.GetProperty("IsZeroOrEmpty", BindingFlags.Public | BindingFlags.Instance));
            var b = p?.GetValue(v) as bool?;
            result = b ?? true;
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Replacement for NavRecord.TestFieldNotBlank(NCLMetaField, NavALErrorInfo).
    /// Real method computes the error message via Session.WindowsCulture, async ALFieldCaption,
    /// PrimaryKeyString, and TryAddTestFieldAction — all of which dereference skeleton state
    /// that's null. Compute the not-blank predicate via the real `GetFieldValue/IsZeroOrEmpty`
    /// (those work on a populated record) and on the throw path raise a NavTestFieldException
    /// directly with InvariantCulture and minimal args, so error code is "TestField" and the
    /// message contains the field caption — what Assert.ExpectedTestFieldError expects.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecord_TestFieldNotBlank(object self, object metaField, object? errorInfo)
    {
        if (metaField == null) throw new ArgumentNullException(nameof(metaField));
        if (TryGetFieldValueIsZeroOrEmpty(self, metaField, out var isBlank) && !isBlank)
            return; // value is set — nothing to assert.
        throw (Exception)CreateNavTestFieldException_Nonblank(metaField);
    }

    /// <summary>
    /// Replacement for NavRecord.TestFieldError(NCLMetaField, string, NavALErrorInfo).
    /// Same rationale as TestFieldNotBlank — computes the error message safely.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void NavRecord_TestFieldError(object self, object metaField, string shouldBeValue, object? errorInfo)
    {
        if (metaField == null) throw new ArgumentNullException(nameof(metaField));
        string current = "<N/A>";
        try
        {
            if (_mGetFieldValue == null)
                _mGetFieldValue = self.GetType().GetMethod("GetFieldValue",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    null, new[] { metaField.GetType() }, null);
            var v = _mGetFieldValue?.Invoke(self, new object[] { metaField });
            current = v?.ToString() ?? "<N/A>";
        }
        catch { /* leave default */ }
        throw (Exception)CreateNavTestFieldException_MustBeEqualTo(metaField, shouldBeValue ?? string.Empty, current);
    }
}
