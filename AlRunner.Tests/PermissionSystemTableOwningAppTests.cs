// PermissionSystemTableOwningAppTests — the runner-side mechanism behind #3695.
//
// WHAT IS BEING PROVED, AND WHAT IS NOT
//   The BC-behaviour claim — that a permission set's `tabledata` grants are readable back from
//   the Permission table (2000000005) with their RIMD mask — is adjudicated upstream by corpus
//   codeunit 60604 "Test Perm. Set Grants", merged in corpus PR #301 and green on all eight
//   cloud legs. That claim does not belong here and is not duplicated here.
//
//   What IS here is the runner-side mechanism the fix turns on, which no corpus test can see:
//   the reflection helper that finds BC's PRIVATE BASE field `NCLMetaApplicationObject.owningApp`.
//   Getting that lookup wrong is the difference between BC's permission graph walk running and
//   BC's permission graph walk raising NullReferenceException on all 47 dependency permission
//   sets — and it went wrong in exactly one specific way while #3695 was implemented, which is
//   what the negative arm below pins.
//
// THE ARM THAT MATTERS
//   FlattenHierarchy_DoesNotFindAPrivateBaseField is the control that makes the positive arm
//   mean something. `BindingFlags.FlattenHierarchy` reads as "search base types too" and does
//   not surface a PRIVATE base field — measured on .NET 8, and it is what the first version of
//   SetOwningApp used, so the field lookup returned null and the loud refusal fired instead of
//   the fix. Without this arm the positive arm would pass just as happily against the broken
//   lookup, because a field declared on the type ITSELF is found by both.
using System;
using System.Reflection;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

public sealed class PermissionSystemTableOwningAppTests
{
    // A two-level hierarchy shaped like BC's: NCLMetaPermissionSet derives from
    // NCLMetaApplicationObject, and `owningApp` is declared PRIVATE on the base.
    private class BaseWithPrivateField
    {
        private object? owningApp;
        internal object? ReadOwningApp() => owningApp;
    }

    private sealed class DerivedLikeMetaPermissionSet : BaseWithPrivateField
    {
    }

    private sealed class DeclaresItOnItself
    {
        private object? owningApp;
        internal object? ReadOwningApp() => owningApp;
    }

    private static FieldInfo? FindUpHierarchy(Type type, string name)
        => (FieldInfo?)typeof(RecordPatches)
            .GetMethod("FindDeclaredFieldUpHierarchy", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, new object?[] { type, name });

    [Fact]
    public void FindDeclaredFieldUpHierarchy_FindsAPrivateFieldDeclaredOnABaseType()
    {
        var field = FindUpHierarchy(typeof(DerivedLikeMetaPermissionSet), "owningApp");

        Assert.NotNull(field);
        Assert.Equal("owningApp", field!.Name);
        // The field it found must be the BASE type's, not something on the derived type —
        // otherwise "found a field" would not mean "found the one BC's property reads".
        Assert.Equal(typeof(BaseWithPrivateField), field.DeclaringType);

        // And it must be WRITABLE through, since writing it is the entire point: BC's
        // NCLMetaApplicationObject.OwningApp getter returns this field and only attempts a
        // lazy resolution when MetadataAppGroup.GroupId != 0, which the runner's base group's
        // id is not.
        var instance = new DerivedLikeMetaPermissionSet();
        var owner = new object();
        field.SetValue(instance, owner);
        Assert.Same(owner, instance.ReadOwningApp());
    }

    [Fact]
    public void FindDeclaredFieldUpHierarchy_FindsAPrivateFieldDeclaredOnTheTypeItself()
    {
        var field = FindUpHierarchy(typeof(DeclaresItOnItself), "owningApp");

        Assert.NotNull(field);
        Assert.Equal(typeof(DeclaresItOnItself), field!.DeclaringType);
    }

    [Fact]
    public void FindDeclaredFieldUpHierarchy_AnswersNullForAFieldNothingInTheHierarchyDeclares()
    {
        // The null is what SetOwningApp turns into a BcShapeGapException naming the member,
        // rather than a NullReferenceException at the first use somewhere BC's own seam would
        // swallow. So "returns null" is a contract, not an implementation detail.
        Assert.Null(FindUpHierarchy(typeof(DerivedLikeMetaPermissionSet), "noSuchFieldAnywhere"));
    }

    [Fact]
    public void FlattenHierarchy_DoesNotFindAPrivateBaseField()
    {
        // THE NEGATIVE CONTROL. This is not a claim about the runner — it is the .NET
        // behaviour that made the first version of SetOwningApp fail, and it is asserted here
        // so the positive arm above cannot be satisfied by re-introducing that lookup.
        var viaFlatten = typeof(DerivedLikeMetaPermissionSet).GetField("owningApp",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.FlattenHierarchy);

        Assert.Null(viaFlatten);
        Assert.NotNull(FindUpHierarchy(typeof(DerivedLikeMetaPermissionSet), "owningApp"));
    }

    [Fact]
    public void PermissionSystemTableId_IsTheTableTheFixRoutes()
    {
        // The dispatch arm in RecordPatches.cs is keyed on this constant, so a wrong value
        // routes some other table's data access through BC's PermissionDataProvider — which
        // would be a silently wrong answer rather than a failure.
        var id = (int)typeof(RecordPatches)
            .GetField("PermissionSystemTableId", BindingFlags.NonPublic | BindingFlags.Static)!
            .GetRawConstantValue()!;

        Assert.Equal(2000000005, id);
    }
}
