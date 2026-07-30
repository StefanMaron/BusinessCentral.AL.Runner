// FieldPoke — assign to private/readonly fields by reflection, falling back to a
// DynamicMethod with skipVisibility when ordinary FieldInfo.SetValue would be rejected
// (e.g. on `readonly` fields that the runtime guards in modern .NET).
using System.Reflection;
using System.Reflection.Emit;

namespace AlRunner.Infrastructure;

internal static class FieldPoke
{
    public static void SetStatic(Type t, string name, object? value)
    {
        var f = t.GetField(name, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (f == null) return;
        try { f.SetValue(null, value); }
        catch (FieldAccessException) { SetStaticReadonly(f, value); }
    }

    public static void TryInitDefault(Type t, string fieldName)
    {
        var f = t.GetField(fieldName, BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (f == null) return;
        try { SetStatic(t, fieldName, Activator.CreateInstance(f.FieldType)); }
        catch { /* optional */ }
    }

    public static void SetInstance(FieldInfo f, object obj, object? value)
    {
        try { f.SetValue(obj, value); }
        catch (FieldAccessException) { SetInstanceReadonly(f, obj, value); }
    }

    private static void SetInstanceReadonly(FieldInfo field, object obj, object? value)
    {
        var dm = new DynamicMethod($"setinst_{field.Name}", typeof(void),
            new[] { typeof(object), typeof(object) },
            field.DeclaringType!.Module, skipVisibility: true);
        var il = dm.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        if (field.FieldType.IsValueType) il.Emit(OpCodes.Unbox_Any, field.FieldType);
        il.Emit(OpCodes.Stfld, field);
        il.Emit(OpCodes.Ret);
        ((Action<object?, object?>)dm.CreateDelegate(typeof(Action<object?, object?>)))(obj, value);
    }

    private static void SetStaticReadonly(FieldInfo field, object? value)
    {
        var dm = new DynamicMethod($"set_{field.Name}", typeof(void), new[] { typeof(object) },
            field.DeclaringType!.Module, skipVisibility: true);
        var il = dm.GetILGenerator();
        if (value == null) il.Emit(OpCodes.Ldnull);
        else
        {
            il.Emit(OpCodes.Ldarg_0);
            if (field.FieldType.IsValueType) il.Emit(OpCodes.Unbox_Any, field.FieldType);
        }
        il.Emit(OpCodes.Stsfld, field);
        il.Emit(OpCodes.Ret);
        ((Action<object?>)dm.CreateDelegate(typeof(Action<object?>)))(value);
    }
}
