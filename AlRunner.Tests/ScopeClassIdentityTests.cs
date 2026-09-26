using System.Linq;
using AlRunner.Rewriters;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;

namespace AlRunner.Tests;

/// <summary>
/// #4666: the pass that gives BC's emitted method-scope classes the IsTest flag and the
/// method id BC's inline-scope emit carries. The AL-observable half (tables 2000000288 and
/// 2000000289) is corpus codeunit 60925; these pin the rewrite itself on the shapes BC's
/// scope-class emit produces.
/// </summary>
public sealed class ScopeClassIdentityTests
{
    // The shape BC's scope-class emit writes: a [NavTest] method constructing its scope class,
    // a procedure with a negative id, and a trigger scope class whose name carries no id.
    private const string Generated = """
        namespace Microsoft.Dynamics.Nav.BusinessApplication
        {
            public sealed class Codeunit65650 : NavTestCodeunit
            {
                [NavTest("A_Test", TestMethodNo = 140), NavFunctionVisibility(FunctionVisibility.External)]
                public void A_Test()
                {
                    using (A_Test_Scope_1286659772 βscope = new A_Test_Scope_1286659772(this))
                    {
                        βscope.Run();
                    }
                }

                [NavName("A_Test")]
                private sealed class A_Test_Scope_1286659772 : NavMethodScope<Codeunit65650>
                {
                    internal A_Test_Scope_1286659772(Codeunit65650 βparent) : base(βparent) { }
                }

                private int Work(int x)
                {
                    using (Work_Scope__1082560015 βscope = new Work_Scope__1082560015(this, x))
                    {
                        βscope.Run();
                        return 0;
                    }
                }

                [NavName("Work")]
                private sealed class Work_Scope__1082560015 : NavMethodScope<Codeunit65650>
                {
                    internal Work_Scope__1082560015(Codeunit65650 βparent, int x) : base(βparent) { }
                }

                [NavName("OnRun")]
                private sealed class OnRun_Scope : NavTriggerMethodScope<Codeunit65650>
                {
                    internal OnRun_Scope(Codeunit65650 βparent) : base(βparent) { }
                }
            }
        }
        """;

    private static ClassDeclarationSyntax ScopeClass(SyntaxTree tree, string name)
        => tree.GetRoot().DescendantNodes().OfType<ClassDeclarationSyntax>()
            .Single(c => c.Identifier.ValueText == name);

    private static string? MethodIdOf(ClassDeclarationSyntax cls)
        => cls.Members.OfType<PropertyDeclarationSyntax>()
            .SingleOrDefault(p => p.ExplicitInterfaceSpecifier?.Name.ToString().EndsWith("IMethodIdProvider") == true
                && p.Identifier.ValueText == "MethodId")
            ?.ExpressionBody?.Expression.ToString();

    private static string? FlagsOverrideOf(ClassDeclarationSyntax cls)
        => cls.Members.OfType<MethodDeclarationSyntax>()
            .SingleOrDefault(m => m.Identifier.ValueText == "GetMethodScopeFlags"
                && m.Modifiers.Any(SyntaxKind.OverrideKeyword))
            ?.ExpressionBody?.Expression.ToString();

    private static bool ImplementsMethodIdProvider(ClassDeclarationSyntax cls)
        => cls.BaseList!.Types.Any(t => t.Type.ToString().EndsWith("IMethodIdProvider"));

    [Fact]
    public void TestScopeClass_GetsItsMethodIdAndTheIsTestFlag()
    {
        var tree = ScopeClassIdentity.Apply(CSharpSyntaxTree.ParseText(Generated));
        var cls = ScopeClass(tree, "A_Test_Scope_1286659772");

        Assert.True(ImplementsMethodIdProvider(cls));
        Assert.Equal("1286659772", MethodIdOf(cls));
        Assert.Equal(
            "base.GetMethodScopeFlags() | global::Microsoft.Dynamics.Nav.Runtime.MethodScopeFlags.IsTest",
            FlagsOverrideOf(cls));
    }

    [Fact]
    public void ProcedureScopeClass_GetsItsNegativeMethodId_AndNoIsTestFlag()
    {
        var tree = ScopeClassIdentity.Apply(CSharpSyntaxTree.ParseText(Generated));
        var cls = ScopeClass(tree, "Work_Scope__1082560015");

        Assert.True(ImplementsMethodIdProvider(cls));
        Assert.Equal("-1082560015", MethodIdOf(cls));
        Assert.Null(FlagsOverrideOf(cls));
    }

    [Fact]
    public void TriggerScopeClass_WhoseNameCarriesNoId_IsLeftAlone()
    {
        var tree = ScopeClassIdentity.Apply(CSharpSyntaxTree.ParseText(Generated));
        var cls = ScopeClass(tree, "OnRun_Scope");

        Assert.Single(cls.BaseList!.Types);
        Assert.Null(MethodIdOf(cls));
        Assert.Null(FlagsOverrideOf(cls));
    }

    [Fact]
    public void TestScopeClass_WithAnEmittedFlagsOverride_KeepsItsFlagsAndAddsIsTest()
    {
        // BC emits this override for an OnPrem-scoped method; its flags must survive.
        var source = Generated.Replace(
            "internal A_Test_Scope_1286659772(Codeunit65650 βparent) : base(βparent) { }",
            "internal A_Test_Scope_1286659772(Codeunit65650 βparent) : base(βparent) { }\n"
            + "protected override MethodScopeFlags GetMethodScopeFlags() { return base.GetMethodScopeFlags() | MethodScopeFlags.IsOnPremiseCallerRequired; }");
        var tree = ScopeClassIdentity.Apply(CSharpSyntaxTree.ParseText(source));
        var cls = ScopeClass(tree, "A_Test_Scope_1286659772");

        var emitted = cls.Members.OfType<MethodDeclarationSyntax>()
            .Single(m => m.Identifier.ValueText == ScopeClassIdentity.EmittedFlagsMethodName);
        Assert.Contains("IsOnPremiseCallerRequired", emitted.Body!.ToString());
        Assert.DoesNotContain(emitted.Modifiers, t => t.IsKind(SyntaxKind.OverrideKeyword));
        Assert.Equal(
            ScopeClassIdentity.EmittedFlagsMethodName + "() | global::Microsoft.Dynamics.Nav.Runtime.MethodScopeFlags.IsTest",
            FlagsOverrideOf(cls));
    }

    [Fact]
    public void ClassNamedLikeAScope_NotDerivingFromNavMethodScope_IsLeftAlone()
    {
        var tree = ScopeClassIdentity.Apply(CSharpSyntaxTree.ParseText(
            "namespace N { public sealed class C { private sealed class Helper_Scope_5 : NavRecordHandle { } } }"));
        var cls = ScopeClass(tree, "Helper_Scope_5");

        Assert.Single(cls.BaseList!.Types);
        Assert.Null(MethodIdOf(cls));
    }

    [Fact]
    public void SourceWithoutAScopeClass_IsReturnedUnchanged()
    {
        var tree = CSharpSyntaxTree.ParseText("namespace N { public sealed class Table18 : NavRecord { } }");
        Assert.Same(tree, ScopeClassIdentity.Apply(tree));
    }

    [Theory]
    [InlineData("Work_Scope_42", 42)]
    [InlineData("Work_Scope__42", -42)]
    [InlineData("A_Scope_Scope_7", 7)]
    [InlineData("Work_Scope__2147483648", int.MinValue)]
    public void MethodIdFromScopeClassName_ReadsBcsEncoding(string name, int expected)
        => Assert.Equal(expected, ScopeClassIdentity.MethodIdFromScopeClassName(name));

    [Theory]
    [InlineData("OnRun_Scope")]
    [InlineData("Codeunit65650")]
    public void MethodIdFromScopeClassName_NoIdInTheName_IsNull(string name)
        => Assert.Null(ScopeClassIdentity.MethodIdFromScopeClassName(name));

    [Fact]
    public void MethodIdFromScopeClassName_IdOutsideInt32_Refuses()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ScopeClassIdentity.MethodIdFromScopeClassName("Work_Scope_2147483648"));
        Assert.Contains("GetMethodScopeName", ex.Message);
    }
}
