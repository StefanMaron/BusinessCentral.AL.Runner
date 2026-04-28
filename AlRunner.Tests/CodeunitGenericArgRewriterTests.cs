using AlRunner;
using Xunit;

namespace AlRunner.Tests;

public class CodeunitGenericArgRewriterTests
{
    private static string WrapInMethod(string body) => $$"""
        namespace Microsoft.Dynamics.Nav.BusinessApplication
        {
            using Microsoft.Dynamics.Nav.Runtime;
            using Microsoft.Dynamics.Nav.Types;
            class C
            {
                void M()
                {
                    {{body}}
                }
            }
        }
        """;

    [Fact]
    public void NavList_CodeunitTypeArg_RewrittenToMockObjectList()
    {
        var input = WrapInMethod("NavList<Codeunit50101> list = NavList<Codeunit50101>.Default;");
        var output = RoslynRewriter.Rewrite(input);

        Assert.Contains("MockObjectList<MockCodeunitHandle>", output);
        Assert.DoesNotContain("NavList<Codeunit50101>", output);
    }

    [Fact]
    public void NavDictionary_CodeunitKey_RewrittenToMockObjectDictionary()
    {
        var input = WrapInMethod("NavDictionary<Codeunit50101, NavText> dict = NavDictionary<Codeunit50101, NavText>.Default;");
        var output = RoslynRewriter.Rewrite(input);

        Assert.Contains("MockObjectDictionary<MockCodeunitHandle, NavText>", output);
        Assert.DoesNotContain("NavDictionary<Codeunit50101, NavText>", output);
    }

    [Fact]
    public void NavDictionary_CodeunitValue_RewrittenToMockObjectDictionary()
    {
        var input = WrapInMethod("NavDictionary<NavText, NavCodeunitHandle> dict = NavDictionary<NavText, NavCodeunitHandle>.Default;");
        var output = RoslynRewriter.Rewrite(input);

        Assert.Contains("MockObjectDictionary<NavText, MockCodeunitHandle>", output);
        Assert.DoesNotContain("NavDictionary<NavText, NavCodeunitHandle>", output);
    }

    [Fact]
    public void NavList_NonCodeunitTypeArg_NotRewritten()
    {
        var input = WrapInMethod("NavList<NavText> list = NavList<NavText>.Default;");
        var output = RoslynRewriter.Rewrite(input);

        Assert.Contains("NavList<NavText>", output);
        Assert.DoesNotContain("MockObjectList<NavText>", output);
    }
}
