// Runner-mechanism tests for #4858: TestPage <field>.Caption() on a control of a PRECOMPILED page
// reads the control's Caption from the dependency's symbol file, and a page-variable control
// stating none answers its control name, never the bound variable's name. The BC-behaviour claim
// is pinned upstream, corpus codeunit 67640; these pin the runner's own lookup without loading the
// BC engine.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Xunit;

namespace AlRunner.Tests;

// RecordPatches' dependency page state resolves through the process-global CacheRoots override
// (DependencyControlDeclaredPropertyTests).
[Collection(CacheRootsSerialCollection.Name)]
public sealed class PageVariableControlCaptionTests
{
    private sealed class FakeExpression(string name, NavValue value)
    {
        public string Name { get; } = name;
        public NavValue Get() => value;
        public void Set(NavValue _) { }
    }

    // Distinctive ids: the dependency-page state is process-global.
    private const int PageId = 88485801;
    private const int DeclaredId = 485800001;    // field(TraceMethodCtl; TraceMethodVar) { Caption = 'Trace Method'; }
    private const int UndeclaredId = 485800002;  // field(Name; TopicName), no Caption
    private const int RecBoundId = 485800003;    // field(Qty; Rec.Quantity) { Caption = 'Control Qty'; }
    private const int UnknownId = 485800099;     // declared by nothing

    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Pages": [
            {
              "Id": 88485801,
              "Name": "PVCC Dep Page",
              "Properties": [ { "Name": "PageType", "Value": "Card" } ],
              "Controls": [
                {
                  "Kind": 1, "Id": 1, "Name": "content",
                  "Controls": [
                    {
                      "Kind": 8, "Id": 485800001, "Name": "TraceMethodCtl",
                      "Properties": [
                        { "Name": "Caption", "Value": "Trace Method" },
                        { "Name": "SourceExpression", "Value": "TraceMethodVar" }
                      ]
                    },
                    {
                      "Kind": 8, "Id": 485800002, "Name": "Name",
                      "Properties": [ { "Name": "SourceExpression", "Value": "TopicName" } ]
                    },
                    {
                      "Kind": 8, "Id": 485800003, "Name": "Qty",
                      "Properties": [
                        { "Name": "Caption", "Value": "Control Qty" },
                        { "Name": "SourceExpression", "Value": "Rec.Quantity" }
                      ]
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static void WithDependencyApp(Action body)
    {
        var dir = TestScratch.Dir("al-runner-page-variable-control-caption-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
            using (var zip = new FileStream(appPath, FileMode.Create))
            using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
                w.Write(SymbolReference);
            RecordPatches.AddBcAppPath(appPath);
            body();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // A form that is not a NavForm has no ControlDefinition for any id and is not a request page:
    // exactly what a precompiled page's synthesized metadata gives the runner.
    private static RunnerPageInstance BuildPage(int pageId)
    {
        var ctor = typeof(RunnerPageInstance).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            types: new[] { typeof(object), typeof(object), typeof(NavRecord), typeof(int), typeof(System.Collections.IDictionary) },
            modifiers: null)
            ?? throw new InvalidOperationException("RunnerPageInstance private ctor not found.");
        return (RunnerPageInstance)ctor.Invoke(new object?[]
            { new object(), new object(), null, pageId, new Dictionary<string, object?>() });
    }

    private static PageVariableTestField Field(int pageId, int controlId, string variableName)
        => new(BuildPage(pageId), new FakeExpression(variableName, new NavText("x")), controlId,
               pageValidationErrors: null);

    [Fact]
    public void Caption_PrecompiledControlStatingCaption_IsThatCaption()
        => WithDependencyApp(() =>
            Assert.Equal("Trace Method", Field(PageId, DeclaredId, "TraceMethodVar").Caption));

    [Fact]
    public void Caption_PrecompiledControlStatingNone_IsTheControlName()
        => WithDependencyApp(() =>
        {
            var field = Field(PageId, UndeclaredId, "TopicName");
            Assert.Equal("Name", field.Caption);
            // Name (#4911) reads the same control name, so the two agree.
            Assert.Equal("Name", field.Name);
        });

    [Fact]
    public void TryGetControlCaption_PrecompiledRecBoundControl_IsItsOwnCaption()
        => WithDependencyApp(() =>
        {
            // LiveNavTestField reads this first, ahead of the field caption.
            var page = BuildPage(PageId);
            Assert.Equal("Control Qty", page.TryGetControlCaption(RecBoundId));
            // A control stating none answers null here, so the field caption chain still runs.
            Assert.Null(page.TryGetControlCaption(UndeclaredId));
        });

    [Fact]
    public void Caption_ControlNoDependencyDeclares_KeepsTheVariableName()
        => WithDependencyApp(() =>
        {
            // Neither an unknown control nor an unknown page borrows another control's caption or name.
            Assert.Equal("SomeVar", Field(PageId, UnknownId, "SomeVar").Caption);
            Assert.Equal("SomeVar", Field(88485802, DeclaredId, "SomeVar").Caption);
        });
}
