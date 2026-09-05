// TableConnectionPatches — issue #2725.
//
// AL's table-connection surface (Database.RegisterTableConnection / HasTableConnection /
// SetDefaultTableConnection / GetDefaultTableConnection / UnregisterTableConnection) is,
// inside Ncl, a set of one-liners through NavSession.TableConnectionManager. The skeleton
// session is built with RuntimeHelpers.GetUninitializedObject, so that property was null,
// and the runner used to answer with two contradictory fakes: a Roslyn shim throwing an
// untyped InvalidOperationException for source-compiled AL, and Cecil no-op / `return
// false` bodies for precompiled callers (Base App, the MS test libraries). Both were silent
// fakes of the kind .claude/rules/loud-failures.md forbids.
//
// BC ships the in-memory implementation itself, so nothing here re-implements it:
//
//   * TableConnectionManager (Ncl) — Register/Has/SetDefault/GetDefault/Unregister are
//     session-local dictionary bookkeeping plus, in Register, a lookup of PERSISTED
//     connections through NavGlobal.AppDatabase.TableConnectionSettingsStorage.Get(...).
//     That lookup is a SQL SELECT against $ndo$tableconnections; the runner has no such
//     rows, so `null` is exactly what a fresh database answers. NclCecilRewrite.Dispatch
//     rewrites Get to return null, and PlantTableConnectionSettingsStorage below gives the
//     skeleton NavDatabase a storage instance so the property chain does not NRE first.
//   * CrmTableConnection (Ncl) — its ctor sets IsTestConnection when the session is
//     executing a test AND the connection string is '@@test@@' (or carries Url=@@test@@).
//     CreateDataAccess then hands out CrmTestDataProvider : TempTableDataProvider, BC's own
//     in-memory store, whose one extra behaviour is assigning Guid.NewGuid() to an empty
//     Guid primary key on insert. NavTestExecution.InTest is already true for the duration
//     of every AL test here (MetadataPatches.EnterTestExecutionScope), so BC's detection
//     runs unmodified.
//
// What stays out of scope is the OTHER branch of CreateDataAccess — CrmDataProvider, a live
// Dataverse connection through the Xrm connector stack — and the ExternalSQL / Exchange /
// MicrosoftGraph connections, none of which has an in-process form. GetExternalDataAccess
// throws RunnerOutOfScopeException there (docs/scope.md#table-connections) instead of
// silently serving those tables from a plain temp store, which is what happened before
// this patch for every TableType the metadata layer did not know about.
//
// Runtime-engine + skeleton-state layer only; no AL business-logic body is touched
// (.claude/rules/precompiled-dll-respect.md).

using System.Reflection;
using System.Runtime.ExceptionServices;
using AlRunner.Infrastructure;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

internal static class TableConnectionPatches
{
    private static bool _resolved;
    private static Type? _tManager;
    private static Type? _tTableConnection;
    private static Type? _tCrmConnection;
    private static Type? _tSettingsStorage;
    private static Type? _tConnectionTypeEnum;
    private static ConstructorInfo? _ctorManager;
    private static FieldInfo? _fSessionManager;
    private static MethodInfo? _mGetCurrentTableConnectionOpen;
    private static MethodInfo? _mCrmCreateDataAccess;
    private static PropertyInfo? _pCrmIsTestConnection;
    private static PropertyInfo? _pConnectionName;
    private static PropertyInfo? _pMetaTableTableType;
    private static object? _skeletonSession;

    /// <summary>The TableType names BC routes through a table connection rather than SQL.
    /// TableType.Query is deliberately absent: BC's QueryTableConnection is a different
    /// mechanism (auto-registered "@Query@") that no AL-declared table uses.</summary>
    private static readonly HashSet<string> ExternalTableTypes =
        new(StringComparer.Ordinal) { "CRM", "ExternalSQL", "Exchange", "MicrosoftGraph" };

    private static void Resolve(Assembly ncl)
    {
        if (_resolved) return;
        _resolved = true;
        _tManager = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.TableConnectionManager");
        _tTableConnection = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.TableConnection");
        _tCrmConnection = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.CrmTableConnection");
        _tSettingsStorage = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.TableConnectionSettingsStorage");
        var tSession = ncl.GetType("Microsoft.Dynamics.Nav.Runtime.NavSession");
        _fSessionManager = tSession?.GetField("<TableConnectionManager>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _ctorManager = _tManager?.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1);
        _mGetCurrentTableConnectionOpen = _tManager?.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "GetCurrentTableConnection" && m.IsGenericMethodDefinition
                                 && m.GetParameters().Length == 1);
        _tConnectionTypeEnum = _mGetCurrentTableConnectionOpen?.GetParameters()[0].ParameterType;
        _mCrmCreateDataAccess = _tCrmConnection?.GetMethods(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(m => m.Name == "CreateDataAccess" && m.GetParameters().Length == 2);
        _pCrmIsTestConnection = _tCrmConnection?.GetProperty("IsTestConnection",
            BindingFlags.NonPublic | BindingFlags.Instance);
        _pConnectionName = _tTableConnection?.GetProperty("Name",
            BindingFlags.NonPublic | BindingFlags.Instance);

        var missing = new List<string>();
        if (_tManager == null) missing.Add("TableConnectionManager");
        if (_ctorManager == null) missing.Add("TableConnectionManager..ctor(ITreeObject)");
        if (_fSessionManager == null) missing.Add("NavSession.<TableConnectionManager>k__BackingField");
        if (_mGetCurrentTableConnectionOpen == null) missing.Add("TableConnectionManager.GetCurrentTableConnection<T>");
        if (_mCrmCreateDataAccess == null) missing.Add("CrmTableConnection.CreateDataAccess(NCLMetaTable, GlobalFilters)");
        if (_pCrmIsTestConnection == null) missing.Add("CrmTableConnection.IsTestConnection");
        if (_tSettingsStorage == null) missing.Add("TableConnectionSettingsStorage");
        if (missing.Count > 0)
            Console.Error.WriteLine("[TableConnectionPatches] Ncl shape changed — not found: "
                + string.Join(", ", missing) + ". Table connections will refuse loudly.");
    }

    /// <summary>
    /// Give the skeleton session the TableConnectionManager its real ctor would have built
    /// (<c>new TableConnectionManager(this)</c>). Must run after the session's TreeRoot is
    /// planted: TreeObject's ctor requires <c>parent.Tree</c> non-null, and the manager's own
    /// methods walk <c>base.Tree.FindParentType&lt;NavSession&gt;()</c> / <c>base.Tree.Session</c>,
    /// both of which resolve through that root.
    /// </summary>
    public static void InstallSkeletonTableConnectionManager(object session)
    {
        Resolve(session.GetType().Assembly);
        _skeletonSession = session;
        if (_ctorManager == null || _fSessionManager == null) return;
        try
        {
            var manager = _ctorManager.Invoke(new[] { session });
            FieldPoke.SetInstance(_fSessionManager, session, manager);
            Console.Error.WriteLine("[TableConnectionPatches] Skeleton NavSession.TableConnectionManager installed");
        }
        catch (Exception ex)
        {
            var inner = ex is TargetInvocationException tie ? tie.InnerException ?? ex : ex;
            Console.Error.WriteLine("[TableConnectionPatches] TableConnectionManager ctor failed — "
                + $"table connections will refuse loudly: {inner.GetType().Name}: {inner.Message}");
        }
    }

    /// <summary>
    /// A reload (edited re-run under --watch / --server, next bundle) is a new session as far
    /// as AL can tell: RecordPatches.ResetForReload drops every in-memory table, and the
    /// connections registered by the previous run must go with them — a CrmTableConnection
    /// caches one CrmTestDataProvider per table id, bound to the OLD bundle's NCLMetaTable,
    /// and would otherwise hand that stale provider to a re-declared table of the same id.
    /// </summary>
    public static void ResetForReload()
    {
        if (_skeletonSession == null || _fSessionManager == null) return;
        var old = _fSessionManager.GetValue(_skeletonSession);
        InstallSkeletonTableConnectionManager(_skeletonSession);
        try { (old as IDisposable)?.Dispose(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[TableConnectionPatches] disposing the previous TableConnectionManager failed: "
                + ex.GetType().Name + ": " + ex.Message);
        }
    }

    /// <summary>
    /// <c>NavDatabase.TableConnectionSettingsStorage</c> is a plain field the real NavDatabase
    /// ctor initialises; on the GetUninitializedObject skeleton it is null, and
    /// TableConnectionManager.RegisterTableConnection / GetCurrentTableConnection dereference
    /// it before anything else can answer. Build BC's own storage object around the skeleton
    /// database. Its <c>Get</c> is Cecil-rewritten to return null (NclCecilRewrite.Dispatch) —
    /// the answer an empty $ndo$tableconnections table gives — so the object never opens a
    /// SQL connection.
    /// </summary>
    public static void PlantTableConnectionSettingsStorage(object skeletonDatabase, Type navDatabaseType)
    {
        Resolve(navDatabaseType.Assembly);
        if (_tSettingsStorage == null) return;
        var field = navDatabaseType.GetField("tableConnectionSettingsStorage",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var ctor = _tSettingsStorage.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .FirstOrDefault(c => c.GetParameters().Length == 1);
        if (field == null || ctor == null)
        {
            Console.Error.WriteLine("[TableConnectionPatches] NavDatabase.tableConnectionSettingsStorage or its ctor "
                + "NOT FOUND — RegisterTableConnection will NRE");
            return;
        }
        if (field.GetValue(skeletonDatabase) != null) return;
        FieldPoke.SetInstance(field, skeletonDatabase, ctor.Invoke(new[] { skeletonDatabase }));
    }

    /// <summary>True when <paramref name="table"/> is declared with a TableType BC serves
    /// through a registered table connection (CRM / ExternalSQL / Exchange / MicrosoftGraph).</summary>
    public static bool IsExternalTableType(NCLMetaTable table, out string tableTypeName)
    {
        tableTypeName = "";
        try
        {
            _pMetaTableTableType ??= table.GetType().GetProperty("TableType",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var name = _pMetaTableTableType?.GetValue(table)?.ToString();
            if (name == null || !ExternalTableTypes.Contains(name)) return false;
            tableTypeName = name;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// BC's DataAccessSource.GetDataAccessForTable for an external TableType:
    /// <c>session.TableConnectionManager.GetCurrentTableConnection&lt;T&gt;(type).CreateDataAccess(table, globalFilters)</c>.
    /// The connection lookup is BC's own — an unregistered type raises BC's
    /// NavNCLArgumentException, not a runner message. A CRM connection registered with the
    /// '@@test@@' string inside a test yields BC's CrmTestDataProvider; every other
    /// connection would open a live external data source and is refused here, by name.
    /// </summary>
    public static object GetExternalDataAccess(object session, NCLMetaTable table, string tableTypeName, object? globalFilters)
    {
        Resolve(session.GetType().Assembly);
        var api = $"Record {table.TableId} (TableType = {tableTypeName})";
        var manager = _fSessionManager?.GetValue(session);
        if (manager == null || _mGetCurrentTableConnectionOpen == null || _tTableConnection == null
            || _tConnectionTypeEnum == null || _mCrmCreateDataAccess == null || _pCrmIsTestConnection == null)
            throw new RunnerOutOfScopeException(api,
                "table-connections — the skeleton session has no TableConnectionManager (Ncl shape changed; "
                + "see [TableConnectionPatches] on stderr)", "table-connections");

        var connectionType = Enum.Parse(_tConnectionTypeEnum, tableTypeName, ignoreCase: false);
        object? connection;
        try
        {
            connection = _mGetCurrentTableConnectionOpen.MakeGenericMethod(_tTableConnection)
                .Invoke(manager, new[] { connectionType });
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            // BC's own "table connection is not registered" error — surface it as BC raised it.
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
        if (connection == null)
            throw new RunnerOutOfScopeException(api,
                $"table-connections — GetCurrentTableConnection<TableConnection>({tableTypeName}) returned null", "table-connections");

        var name = _pConnectionName?.GetValue(connection)?.ToString() ?? "";
        if (_tCrmConnection != null && _tCrmConnection.IsInstanceOfType(connection)
            && _pCrmIsTestConnection.GetValue(connection) is true)
        {
            try
            {
                return _mCrmCreateDataAccess.Invoke(connection, new[] { table, globalFilters })!;
            }
            catch (TargetInvocationException tie) when (tie.InnerException != null)
            {
                ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                throw;
            }
        }

        throw new RunnerOutOfScopeException(
            $"{connection.GetType().Name}.CreateDataAccess for {api} via table connection '{name}'",
            "table-connections — a live external data source (Dataverse/CRM, external SQL, Exchange, "
            + "Microsoft Graph) needs the service tier's connector stack; only a CRM connection registered "
            + "with the '@@test@@' connection string inside a test runs in-process, on BC's own CrmTestDataProvider",
            "table-connections");
    }
}
