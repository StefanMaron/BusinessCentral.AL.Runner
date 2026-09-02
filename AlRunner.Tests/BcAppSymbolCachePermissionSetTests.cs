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
//  2. `Assignable` is not always stated. Base Application 28.1's "D365 Basic - Edit" (208)
//     declares no Assignable property and is assignable; "LOCAL" (1001) declares
//     `Assignable = false`. AL's default is true, matching table 2000000250's own field 4
//     (`InitValue = true`).
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
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            var symbols = BcAppSymbolCache.Get(appPath);

            Assert.NotNull(symbols.PermissionSets);
            // Both the nested three and the root-level one; a root-only read would find 1.
            Assert.Equal(4, symbols.PermissionSets!.Count);

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

    [Fact]
    public void PermissionSets_AssignableDefaultsToTrue_AndAnExplicitFalseIsHonored()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var appPath = WriteApp(dir, SymbolReference);
            var permissionSets = BcAppSymbolCache.Get(appPath).PermissionSets!;

            // Declares Assignable = 0.
            var agentObjects = Assert.Single(permissionSets, p => p.Name == "Agent - Objects");
            Assert.False(agentObjects.Assignable);

            // Declares no Assignable property at all — AL's default is true.
            var basicEdit = Assert.Single(permissionSets, p => p.Name == "D365 Basic - Edit");
            Assert.True(basicEdit.Assignable);
            Assert.Equal("Dynamics 365 Basic - Edit access", basicEdit.Caption);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PermissionSets_NoDeclaredCaption_StaysNull()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
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
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
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
