// BcAppSymbolCachePermissionSetTests — proves BcAppSymbolCache reads the permission sets a
// dependency .app declares, which is what the "Metadata Permission Set" (2000000250) virtual
// table serves (issue #2313).
//
// Gap being fixed
// ---------------
// The table was empty, so Microsoft's "Users - Create Super User" (codeunit 9000) could not
// resolve MetadataPermissionSet.Get(<null guid>, 'SUPER') and every AL test that creates a
// user failed in setup.
//
// The two things this parse gets wrong if written carelessly, both pinned below:
//
//  1. BC 26+ nests application objects under "Namespaces". A root-only read of
//     "PermissionSets" finds 2 entries in Base Application 28.1 and ZERO in System
//     Application 28.1 — which is where SUPER lives. Measured against the real .app files,
//     not assumed.
//  2. `Assignable` is not always stated, and an absent one reads as FALSE — what BC's own
//     reader answers, not AL's source-language default (#2417, #3806). Base Application 28.1's
//     "D365 Basic - Edit" (208) and "D365 Basic - Read" (209) and System Application's
//     "System Execute - Basic" (68) declare none; "LOCAL" (1001) declares `Assignable = false`.
//     Table 2000000250's field 4 carries `InitValue = true`, which is an AL initial value for a
//     NEW record and does not describe how BC reads an emitted document — the reading that made
//     this wrong for four issues.
//
// The shapes below mirror what those real .app symbol files state, including SUPER's caption
// and the System Application's app id. The .app shape (a plain zip holding
// SymbolReference.json) mirrors BcAppSymbolCachePageMetadataTests.

using System.IO.Compression;
using System.Text;
using AlRunner.Patches;
using Xunit;

namespace AlRunner.Tests;

// #1821: BcAppSymbolCache.Get() resolves its on-disk path through the process-global
// CacheRoots override, so this joins CacheRootsSerialCollection to avoid racing
// CacheRootsTests's SetOverride calls — see that collection's header for why.
[Collection(CacheRootsSerialCollection.Name)]
public class BcAppSymbolCachePermissionSetTests
{
    private const string SystemApplicationAppId = "63ca2fa4-4f03-4f2b-a480-172fef340d3f";

    private static string WriteApp(string dir, string symbolReferenceJson)
    {
        var appPath = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".app");
        using var zip = new FileStream(appPath, FileMode.Create);
        using var za = new ZipArchive(zip, ZipArchiveMode.Create);
        var entry = za.CreateEntry("SymbolReference.json");
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(symbolReferenceJson);
        return appPath;
    }

    // "SUPER" and "Agent - Objects" sit inside a Namespaces container, exactly as the real
    // System Application 28.1 symbol file states them. "Root Level Set" sits at the root, so
    // one traversal has to find both.
    private const string SymbolReference = """
        {
          "RuntimeVersion": "15.1",
          "AppId": "63ca2fa4-4f03-4f2b-a480-172fef340d3f",
          "Name": "System Application",
          "PermissionSets": [
            {
              "Id": 9001,
              "Name": "Root Level Set",
              "Properties": [
                { "Name": "Caption", "Value": "Declared at the symbol reference root" },
                { "Name": "Assignable", "Value": "1" }
              ]
            }
          ],
          "Namespaces": [
            {
              "Name": "System",
              "Namespaces": [
                {
                  "Name": "Security",
                  "PermissionSets": [
                    {
                      "Id": 31,
                      "Name": "SUPER",
                      "Properties": [
                        { "Name": "Access", "Value": "Public" },
                        { "Name": "Assignable", "Value": "1" },
                        { "Name": "Caption", "Value": "This role has all permissions." }
                      ]
                    },
                    {
                      "Id": 4300,
                      "Name": "Agent - Objects",
                      "Properties": [
                        { "Name": "Access", "Value": "Internal" },
                        { "Name": "Assignable", "Value": "0" }
                      ]
                    },
                    {
                      "Id": 208,
                      "Name": "D365 Basic - Edit",
                      "Properties": [
                        { "Name": "Caption", "Value": "Dynamics 365 Basic - Edit access" }
                      ]
                    },
                    {
                      "Id": 68,
                      "Name": "System Execute - Basic"
                    }
                  ]
                }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void PermissionSets_NestedUnderNamespaces_AreFoundWithTheirOwningAppId()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-permset-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            var symbols = BcAppSymbolCache.Get(appPath);

            Assert.NotNull(symbols.PermissionSets);
            // Both the nested four and the root-level one; a root-only read would find 1.
            Assert.Equal(5, symbols.PermissionSets!.Count);

            var super = Assert.Single(symbols.PermissionSets, p => p.Name == "SUPER");
            Assert.Equal(31, super.Id);
            Assert.Equal("This role has all permissions.", super.Caption);
            Assert.True(super.Assignable);

            var rootLevel = Assert.Single(symbols.PermissionSets, p => p.Name == "Root Level Set");
            Assert.Equal(9001, rootLevel.Id);
            Assert.Equal("Declared at the symbol reference root", rootLevel.Caption);

            // The owning app id is the symbol reference's own AppId — one value for every
            // permission set in the file, which is why PermissionSetSymbol does not repeat
            // it. Blanking it for SUPER/SECURITY is the virtual table's rule (BC's
            // SystemTableTriggers.IsPermissionSetAppIdNull), applied at row build time, so
            // the parse stays a faithful reading of the symbol file.
            Assert.Equal(SystemApplicationAppId, symbols.AppId);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// An ABSENT <c>Assignable</c> reads as FALSE, in both shapes the real symbol files use, and
    /// an explicit "1" still reads as true — so a reader that simply answered false everywhere
    /// would fail here (#2417, #3806).
    ///
    /// <para>CLAIM: BC's own reader never defaults this to true. <c>MetaPermissionSet.Create</c>
    /// assigns <c>Assignable</c> only inside <c>case 10: if (name == "Assignable")</c>, and
    /// <c>MetaPermissionSet()</c> initialises only Permissions/Included/Excluded — so an absent
    /// attribute leaves <c>default(bool)</c>, which is false. Measured on
    /// Microsoft.Dynamics.Nav.Types.dll 28.1.49838.53910 (sha256 c91ede8f…); #3806 measured the
    /// same build's reader answering <c>Assignable = False</c> for set 68.</para>
    ///
    /// <para>TRAP: there are TWO absent shapes and a fixture with only one proves half the fix.
    /// Base Application 208/209 state a <c>Properties</c> array with no <c>Assignable</c> key;
    /// System Application 68 states no <c>Properties</c> key at all. Measured across the real
    /// 28.1 .app files: 436 permission sets, 121 state "1", 312 state "0", and exactly these 3
    /// state none.</para>
    /// </summary>
    [Fact]
    public void PermissionSets_AbsentAssignableReadsAsFalse_AndAnExplicitTrueIsHonored()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-permset-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            var permissionSets = BcAppSymbolCache.Get(appPath).PermissionSets!;

            // Declares Assignable = 0.
            var agentObjects = Assert.Single(permissionSets, p => p.Name == "Agent - Objects");
            Assert.False(agentObjects.Assignable);

            // Declares Assignable = 1 — the direction that fails if the fix over-corrects to
            // "always false".
            var super = Assert.Single(permissionSets, p => p.Name == "SUPER");
            Assert.True(super.Assignable);

            // Absent shape 1: a Properties array that carries no Assignable key (Base
            // Application 208 "D365 Basic - Edit", verbatim).
            var basicEdit = Assert.Single(permissionSets, p => p.Name == "D365 Basic - Edit");
            Assert.False(basicEdit.Assignable);
            // The rest of the parse is untouched by the defaulting change.
            Assert.Equal("Dynamics 365 Basic - Edit access", basicEdit.Caption);

            // Absent shape 2: no Properties key whatsoever — System Application 68
            // "System Execute - Basic", the exact set #2417 was filed about.
            var systemExecuteBasic = Assert.Single(permissionSets, p => p.Name == "System Execute - Basic");
            Assert.Equal(68, systemExecuteBasic.Id);
            Assert.False(systemExecuteBasic.Assignable);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PermissionSets_NoDeclaredCaption_StaysNull()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-permset-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            var permissionSets = BcAppSymbolCache.Get(appPath).PermissionSets!;

            // "not declared" must stay distinguishable from "declared as something", so the
            // parse leaves it null. The virtual table then writes BC's own answer for a
            // permission set with no caption, which is the empty string
            // (NCLMetaPermissionSet.Caption is `captionStrings?.GetValueOrDefault() ?? ""`),
            // never the role id.
            var agentObjects = Assert.Single(permissionSets, p => p.Name == "Agent - Objects");
            Assert.Null(agentObjects.Caption);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PermissionSets_AppDeclaringNone_ReturnsAnEmptyList()
    {
        var dir = TestScratch.Dir("al-runner-bcsym-permset-tests");
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, """
                {
                  "RuntimeVersion": "15.1",
                  "AppId": "c1335042-3002-4257-bf8a-75c898ccb1b8",
                  "Name": "Application",
                  "Codeunits": [ { "Id": 1, "Name": "Some Codeunit" } ]
                }
                """);

            var permissionSets = BcAppSymbolCache.Get(appPath).PermissionSets;

            // Empty, not null: an app that declares none must not be indistinguishable from
            // one whose payload predates the field (that case is what the CacheVersion bump
            // exists to make impossible).
            Assert.NotNull(permissionSets);
            Assert.Empty(permissionSets!);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
