using System;
using AlRunner.Infrastructure;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// Pins <see cref="PrivateMemberLookup"/> against the two shapes the runner actually meets, because
/// a repair that handles only one of them shipped and broke CI (issue #2725).
///
/// <para>The runner reflects on <c>TempTableDataProvider.primaryTree</c> (a private FIELD) and
/// <c>TempTableDataProvider.FindImplementation</c> (a private METHOD). BC's own
/// <c>CrmTableConnection.CrmTestDataProvider</c> derives from that type, and
/// <c>GetField/GetMethod(NonPublic)</c> on a derived type does not return a base class's private
/// members — so asking the runtime type returns null and the runner throws on a CRM test
/// connection.</para>
///
/// <para>The first repair climbed base types until it found one NAMED "TempTableDataProvider".
/// That inverts the bug: a provider that declares the member ITSELF gets climbed straight past to
/// <see cref="object"/> and loses it. Both RowVersionPatchesTests insert cases failed exactly that
/// way. So both directions are asserted here, not just the reported one.</para>
/// </summary>
public class PrivateMemberLookupTests
{
    private class DeclaresItself
    {
        private readonly string primaryTree = "self";
        private string FindImplementation() => "self";
    }

    private class BaseDeclares
    {
        private readonly string primaryTree = "base";
        private string FindImplementation() => "base";
    }

    private class DerivedDeclaresNothing : BaseDeclares { }

    private class DerivedShadows : BaseDeclares
    {
        // Same name again on the derived type, which is legal in C# and is why the walk must
        // stop at the FIRST level that declares it rather than at the deepest one.
        private readonly string primaryTree = "derived";
        private string FindImplementation() => "derived";
    }

    private class DeclaresNeither { }

    [Fact]
    public void Field_TypeDeclaringItItself_IsFound()
    {
        var f = PrivateMemberLookup.Field(typeof(DeclaresItself), "primaryTree");
        Assert.NotNull(f);
        Assert.Equal(typeof(DeclaresItself), f!.DeclaringType);
        Assert.Equal("self", f.GetValue(new DeclaresItself()));
    }

    [Fact]
    public void Field_DeclaredOnlyOnTheBase_IsFoundThroughTheDerivedType()
    {
        var f = PrivateMemberLookup.Field(typeof(DerivedDeclaresNothing), "primaryTree");
        Assert.NotNull(f);
        Assert.Equal(typeof(BaseDeclares), f!.DeclaringType);
        Assert.Equal("base", f.GetValue(new DerivedDeclaresNothing()));
    }

    [Fact]
    public void Field_ShadowedOnTheDerivedType_ResolvesTheDerivedOne()
    {
        var f = PrivateMemberLookup.Field(typeof(DerivedShadows), "primaryTree");
        Assert.NotNull(f);
        Assert.Equal(typeof(DerivedShadows), f!.DeclaringType);
        Assert.Equal("derived", f.GetValue(new DerivedShadows()));
    }

    [Fact]
    public void Field_NobodyDeclaresIt_IsNull() =>
        Assert.Null(PrivateMemberLookup.Field(typeof(DeclaresNeither), "primaryTree"));

    [Fact]
    public void Method_TypeDeclaringItItself_IsFound()
    {
        var m = PrivateMemberLookup.Method(typeof(DeclaresItself), "FindImplementation");
        Assert.NotNull(m);
        Assert.Equal("self", m!.Invoke(new DeclaresItself(), null));
    }

    [Fact]
    public void Method_DeclaredOnlyOnTheBase_IsFoundThroughTheDerivedType()
    {
        var m = PrivateMemberLookup.Method(typeof(DerivedDeclaresNothing), "FindImplementation");
        Assert.NotNull(m);
        Assert.Equal(typeof(BaseDeclares), m!.DeclaringType);
        Assert.Equal("base", m.Invoke(new DerivedDeclaresNothing(), null));
    }

    [Fact]
    public void Method_NobodyDeclaresIt_IsNull() =>
        Assert.Null(PrivateMemberLookup.Method(typeof(DeclaresNeither), "FindImplementation"));

    [Fact]
    public void FitsInstance_MemberFromAnUnrelatedType_IsRejected()
    {
        // The reason the call sites re-resolve instead of trusting their static memo: a member
        // resolved from one provider shape must not be reused against another.
        var fromSelf = PrivateMemberLookup.Field(typeof(DeclaresItself), "primaryTree");
        Assert.True(PrivateMemberLookup.FitsInstance(fromSelf, new DeclaresItself()));
        Assert.False(PrivateMemberLookup.FitsInstance(fromSelf, new BaseDeclares()));
        Assert.False(PrivateMemberLookup.FitsInstance(null, new DeclaresItself()));
    }

    [Fact]
    public void FitsInstance_BaseDeclaredMember_FitsADerivedInstance()
    {
        var fromBase = PrivateMemberLookup.Field(typeof(BaseDeclares), "primaryTree");
        Assert.True(PrivateMemberLookup.FitsInstance(fromBase, new DerivedDeclaresNothing()));
    }

    /// <summary>
    /// The exact defect that shipped, written as a test so it cannot come back: climbing to a type
    /// of a KNOWN NAME loses the member whenever the name never appears in the hierarchy.
    /// </summary>
    [Fact]
    public void NameBasedClimb_LosesAMemberTheTypeDeclaresItself_HierarchyWalkDoesNot()
    {
        var t = typeof(DeclaresItself);
        var climbed = t;
        while (climbed.BaseType != null && climbed.Name != "TempTableDataProvider")
            climbed = climbed.BaseType;
        Assert.Equal(typeof(object), climbed);
        Assert.Null(climbed.GetField("primaryTree",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance));

        Assert.NotNull(PrivateMemberLookup.Field(t, "primaryTree"));
    }
}
