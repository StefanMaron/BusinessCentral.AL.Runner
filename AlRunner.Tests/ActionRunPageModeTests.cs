// The runner-side half of #4997: how an action's RunPageMode is read, from BC's compiled
// ActionDefinition for a page this run compiles and from SymbolReference.json for a precompiled
// one, and the pending-mode channel that carries Create to the handler page. The AL-observable
// claim (the opened page's editability, and a new record for Create) is measured upstream by
// corpus codeunits 67018 / 67019 (StefanMaron/BusinessCentral.AL.Language.Tests#509).
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using AlRunner.Infrastructure;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Types.Metadata;
using Xunit;

namespace AlRunner.Tests;

// BcAppSymbolCache.Get() resolves through the process-global CacheRoots override, the same
// reason ActionRunPageViewTests serialises.
[Collection(CacheRootsSerialCollection.Name)]
public sealed class ActionRunPageModeTests
{
    private const int HostPageId = 88249752;
    private const int ViewActionId = 649750001;
    private const int CreateActionId = 649750002;
    private const int NoModeActionId = 649750003;

    private const string SymbolReference = """
        {
          "RuntimeVersion": "17.0",
          "Pages": [
            {
              "Id": 88249752,
              "Name": "ARPMT Host",
              "Properties": [ { "Name": "PageType", "Value": "Card" } ],
              "Actions": [
                {
                  "Id": 2, "Name": "processing",
                  "Actions": [
                    { "Kind": 2, "Id": 649750001, "Name": "Open View",
                      "Properties": [
                        { "Name": "RunObject", "Value": "ARPMT Target" },
                        { "Name": "RunPageMode", "Value": "View" }
                      ] },
                    { "Kind": 2, "Id": 649750002, "Name": "Open Create",
                      "Properties": [
                        { "Name": "RunObject", "Value": "ARPMT Target" },
                        { "Name": "RunPageMode", "Value": "Create" }
                      ] },
                    { "Kind": 2, "Id": 649750003, "Name": "Open Default",
                      "Properties": [
                        { "Name": "RunObject", "Value": "ARPMT Target" }
                      ] }
                  ]
                }
              ]
            }
          ]
        }
        """;

    private static void WithApp(Action<string> body)
    {
        var dir = TestScratch.Dir("al-runner-action-runpagemode-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
            using (var zip = new FileStream(appPath, FileMode.Create))
            using (var za = new ZipArchive(zip, ZipArchiveMode.Create))
            using (var w = new StreamWriter(za.CreateEntry("SymbolReference.json").Open(), Encoding.UTF8))
                w.Write(SymbolReference);
            body(appPath);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static Dictionary<int, BcAppSymbolCache.ActionRunObjectSymbol> RunObjects(string appPath)
        => Assert.Single(BcAppSymbolCache.Get(appPath).Pages, p => p.Id == HostPageId).MemberIdToRunObject!;

    // RunPageModeFromText reads only _pageId (for the refusal message) from the instance.
    private static RunnerPageInstance Instance()
    {
        var instance = (RunnerPageInstance)RuntimeHelpers.GetUninitializedObject(typeof(RunnerPageInstance));
        typeof(RunnerPageInstance).GetField("_pageId", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(instance, HostPageId);
        return instance;
    }

    [Fact]
    public void SymbolReader_CarriesEachActionsRunPageModeVerbatim()
        => WithApp(app =>
        {
            var runObjects = RunObjects(app);
            Assert.Equal("View", runObjects[ViewActionId].RunPageMode);
            Assert.Equal("Create", runObjects[CreateActionId].RunPageMode);
            Assert.Null(runObjects[NoModeActionId].RunPageMode);
        });

    [Theory]
    [InlineData("View", RunnerPageInstance.ActionRunPageMode.View)]
    [InlineData("Edit", RunnerPageInstance.ActionRunPageMode.Edit)]
    [InlineData("Create", RunnerPageInstance.ActionRunPageMode.Create)]
    [InlineData("create", RunnerPageInstance.ActionRunPageMode.Create)]
    [InlineData(null, RunnerPageInstance.ActionRunPageMode.Default)]
    public void RunPageModeFromText_MapsAlsThreeModes(string? text, RunnerPageInstance.ActionRunPageMode expected)
        => Assert.Equal(expected, Instance().RunPageModeFromText(4242, text));

    [Fact]
    public void RunPageModeFromText_AnUnknownMode_IsRefusedByName()
    {
        var ex = Assert.Throws<RunnerOutOfScopeException>(() => Instance().RunPageModeFromText(4242, "Sideways"));
        Assert.Contains("TestPage action 4242", ex.Message);
        Assert.Contains("RunPageMode = 'Sideways'", ex.Message);
    }

    // The compiled-metadata route passes BC's own enum member NAME through RunPageModeFromText,
    // so every member of that enum has to land on a mode rather than on the refusal.
    [Fact]
    public void EveryMemberOfBcsRunPageModeEnum_MapsToAMode()
    {
        var enumType = typeof(ActionDefinition).GetProperty("RunPageMode")!.PropertyType;
        var names = Enum.GetNames(enumType);
        Assert.NotEmpty(names);
        foreach (var name in names)
            Assert.NotEqual(RunnerPageInstance.ActionRunPageMode.Default, Instance().RunPageModeFromText(4242, name));
    }

    [Fact]
    public void PendingCreate_IsConsumedOnceByItsPageAndMarksTheFormForANewRecord()
    {
        try
        {
            RunnerPendingPageOpenMode.ArmCreate(4711);

            Assert.False(RunnerPendingPageOpenMode.TryConsume(4712, out _, out _));
            Assert.True(RunnerPendingPageOpenMode.TryConsume(4711, out var readOnly, out var create));
            Assert.False(readOnly);
            Assert.True(create);
            Assert.False(RunnerPendingPageOpenMode.TryConsume(4711, out _, out _));

            var form = new object();
            Assert.False(RunnerPendingPageOpenMode.TryConsumeOpensOnNewRecord(form));
            RunnerPendingPageOpenMode.MarkOpensOnNewRecord(form);
            Assert.True(RunnerPendingPageOpenMode.TryConsumeOpensOnNewRecord(form));
            Assert.False(RunnerPendingPageOpenMode.TryConsumeOpensOnNewRecord(form));
        }
        finally { RunnerPendingPageOpenMode.Disarm(); }
    }

    [Fact]
    public void PendingView_IsReadOnlyAndNotCreate()
    {
        try
        {
            RunnerPendingPageOpenMode.ArmCreate(4711);
            RunnerPendingPageOpenMode.Arm(4711, readOnly: true);

            Assert.True(RunnerPendingPageOpenMode.TryConsume(4711, out var readOnly, out var create));
            Assert.True(readOnly);
            Assert.False(create);
        }
        finally { RunnerPendingPageOpenMode.Disarm(); }
    }
}
