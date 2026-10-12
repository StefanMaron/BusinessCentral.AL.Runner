// Rename of an AutoIncrement field's value is refused (#5517).
//
// BC's SQL layer refuses an UPDATE that writes an AutoIncrement column (error code 18023796); the
// catch in RecordImplementation.RenameRecordAsync maps it to "You cannot change the value of an
// AutoIncrement field." plus the Field / Table / Identification lines. The runner's in-memory
// provider has no such constraint, so this prepend raises the same exception BC's mapping builds.
// Claim and citation: corpus 67822 AutoInc_Rename_OfTheAutoIncrementKey_IsRefused, ten cloud legs and
// the Windows container (AlRunner#5517); the mapping is RecordImplementationHelper.MapException, Ncl 28.4.
using System.Reflection;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private const string AutoIncrementRenameSurface = "autoincrement-rename";
    private const string AutoIncrementRenameDetail = "BC shape changed; see #5517";
    private const int AutoIncrementFieldErrorCode = 18023796;

    /// <summary>
    /// Prepended to RecordImplementation.RenameRecordAsync(DataError, NavRecord) with the record being
    /// renamed (<paramref name="original"/>, old key) and the clone carrying the new key
    /// (<paramref name="renamed"/>). Raises BC's AutoIncrement-field error when the AutoIncrement field's
    /// value differs between the two. Temporary records are skipped: no SQL UPDATE is involved.
    /// </summary>
    public static void GuardRenameOfAutoIncrementField(NavRecord original, NavRecord renamed)
    {
        if (original.IsTemporary) return;
        if (!BcRuntime.TryGetAutoIncrementField(original.MetaTable.TableId, out int aiFieldNo)
            || !original.MetaTable.TryGetFieldByNo(aiFieldNo, out var aiField))
            return;
        if (!TryToLong(original.GetFieldValue(aiField), out long oldValue)
            || !TryToLong(renamed.GetFieldValue(aiField), out long newValue)
            || oldValue == newValue)
            return;
        throw BuildAutoIncrementFieldError(original, renamed, aiField);
    }

    private static string TableCaptionOf(NCLMetaTable meta)
        => (string?)BcShape.Property(typeof(NCLMetaTable), "TableCaptionSafe",
               BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, AutoIncrementRenameSurface)
               .GetValue(meta) ?? string.Empty;

    private static Exception BuildAutoIncrementFieldError(NavRecord original, NavRecord renamed, NCLMetaField aiField)
    {
        var csideType = ALDatabasePatches.ResolveNavCSideExceptionType()
            ?? throw new BcShapeGapException(AutoIncrementRenameSurface, "NavCSideException",
                $"type not found — {AutoIncrementRenameDetail}");
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        var ctor = csideType.GetConstructor(all, null, new[] { typeof(int), typeof(string) }, null)
            ?? throw new BcShapeGapException(AutoIncrementRenameSurface, "NavCSideException..ctor(int,string)",
                $"constructor not found — {AutoIncrementRenameDetail}");
        var create = BcShape.RequiredMethod(csideType, "CreateAutoIncrementFieldError", all,
            AutoIncrementRenameSurface, "NavCSideException.CreateAutoIncrementFieldError", AutoIncrementRenameDetail);
        var format = BcShape.RequiredMethod(
            typeof(NavRecord).Assembly.GetType("Microsoft.Dynamics.Nav.Runtime.RecordImplementationHelper")
                ?? throw new BcShapeGapException(AutoIncrementRenameSurface, "RecordImplementationHelper",
                    $"type not found — {AutoIncrementRenameDetail}"),
            "FormatPrimaryKeyFieldsAndValues", all,
            AutoIncrementRenameSurface, "RecordImplementationHelper.FormatPrimaryKeyFieldsAndValues",
            AutoIncrementRenameDetail);

        // BC formats the identification from the buffer it was about to write, the renamed key.
        var renamedImpl = RecordImplementationOf(renamed);
        var bufferField = RequiredField(renamedImpl.GetType(), "mutableRecordBuffer", AutoIncrementRenameSurface);
        var identification = (string?)format.Invoke(null, new[] { bufferField.GetValue(renamedImpl) })
            ?? string.Empty;

        var inner = ctor.Invoke(new object[] { AutoIncrementFieldErrorCode, string.Empty });
        return (Exception)create.Invoke(null, new object[]
        {
            inner, aiField.FieldCaption, TableCaptionOf(original.MetaTable), identification, true,
        })!;
    }
}
