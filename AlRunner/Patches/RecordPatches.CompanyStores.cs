// RecordPatches.CompanyStores — one row store per (table, company) for a per-company table.
//
// BC keeps a per-company table's rows apart by company token: a record opened on another company
// (Record.ChangeCompany, Record.CurrentCompany over a company name, RecordRef.ChangeCompany) reads
// and writes that company's own rows. The runner's store is one in-memory TempTableDataProvider per
// (DataAccessSource, table) that ignores the token, so before #5349 every company but the session's
// own was refused (CompanyAccessPatches), because granting it would have shared one set of rows.
//
// HOW IT IS DONE
//   The session company is token 0 and keeps the store GetDataAccessForTableCore hands out. Every
//   other company gets a SIBLING store of its own, created lazily the first time a record on that
//   company reads RecordImplementation.dataAccess, and held weakly against the session-company
//   store it shadows (_companyStores). Because the siblings hang off that store, they die with it:
//   ResetPerTestState and the install-baseline restore replace the session-company store at a
//   test-codeunit boundary, and the other companies' rows go with it, exactly as the Company row
//   that made them accessible does.
//
//   The routing point is the read hook BC's record code already goes through,
//   RecordImplementation_LiveDataAccess (every `ldfld RecordImplementation.dataAccess`, #4781). It
//   asks the record's own TableState for its company token and swaps in the sibling store. Nothing
//   is done until a non-session company has been granted (_anyCompanyDataAccess), so a run that
//   never calls ChangeCompany pays one volatile read per DataAccess read.
//
// WHAT IS NOT ROUTED
//   Anything that asks DataAccessSource.GetDataAccessForTable for a table WITHOUT a record in hand
//   still gets the session-company store: RecordImplementation.ValidateRelation (TableRelation
//   validation), NavDataTransfer, Query objects, and the runner's own AutoIncrement high-water
//   read. A record on another company that validates a TableRelation therefore checks the
//   session company's related rows. FlowField sources ARE routed (FlowFieldPatches asks
//   GetDataAccessForTableInCompany with the record's token). See docs/limitations.md.
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Dynamics.Nav.Runtime;

namespace AlRunner.Patches;

public static partial class RecordPatches
{
    private const string CompanyStoreSurface = "per-company record stores (Record.ChangeCompany)";

    // session-company store -> (company token -> that company's store). Weak on the key, so the
    // siblings are collected with the store they shadow.
    private static readonly ConditionalWeakTable<object, ConcurrentDictionary<int, object>> _companyStores = new();

    // Session-company stores that no company routing applies to (temporary, not per company, a
    // system or virtual table, not one a perTable handed out), so the answer is computed once.
    private static readonly ConditionalWeakTable<object, object> _notCompanyRouted = new();

    private static volatile bool _anyCompanyDataAccess;

    private static FieldInfo? _fRecImplTableStateForCompany, _fRecImplMetaTableForCompany, _fTableStateCompanyToken;

    /// <summary>
    /// Called by <see cref="CompanyAccessPatches"/> the first time it grants a company that is not
    /// the session's own. Until then no record can carry a non-zero company token, which is what
    /// lets <see cref="RecordImplementation_LiveDataAccess"/> skip the routing walk entirely.
    /// </summary>
    internal static void NoteNonSessionCompanyGranted() => _anyCompanyDataAccess = true;

    private static PropertyInfo? _pNavRecordImplementation;

    private static object RecordImplementationOf(NavRecord record)
    {
        _pNavRecordImplementation ??= AlRunner.Infrastructure.BcShape.Property(
            typeof(NavRecord), "RecordImplementation",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, CompanyStoreSurface);
        return _pNavRecordImplementation.GetValue(record)
            ?? throw new InvalidOperationException("NavRecord.RecordImplementation is null");
    }

    /// <summary>The company token a record currently carries, 0 for the session's own company.</summary>
    private static int RecordCompanyToken(object recordImplementation)
    {
        var riType = recordImplementation.GetType();
        _fRecImplTableStateForCompany ??= RequiredField(riType, "tableState", CompanyStoreSurface);
        var tableState = _fRecImplTableStateForCompany.GetValue(recordImplementation);
        if (tableState == null) return 0;
        _fTableStateCompanyToken ??= RequiredField(tableState.GetType(), "companyNameToken", CompanyStoreSurface);
        return _fTableStateCompanyToken.GetValue(tableState) is int token ? token : 0;
    }

    private static PropertyInfo? _pRecImplSession, _pSessionDatabase, _pDatabaseCompanyTokens;
    private static MethodInfo? _mCompanyTokensGetByToken;

    /// <summary>
    /// The company name a record on a non-session company reports to BC's own record-cloning code
    /// (<c>RecordImplementation.GetActiveCompany</c>, read by <c>NavRecord.CloneRecord(keepCompany)</c>,
    /// which <c>DeleteAll</c> with triggers and every row-wise bulk write build their working record
    /// from). It is the name BC's own token table holds for the record's token, so the clone resolves
    /// to the same company and the same store. The session company is <c>""</c>, token 0's name.
    /// </summary>
    internal static string ActiveCompanyNameOf(object recordImplementation)
    {
        if (!_anyCompanyDataAccess) return string.Empty;
        var token = RecordCompanyToken(recordImplementation);
        if (token == 0) return string.Empty;

        var riType = recordImplementation.GetType();
        _pRecImplSession ??= AlRunner.Infrastructure.BcShape.Property(
            riType, "Session", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, CompanyStoreSurface);
        var session = _pRecImplSession.GetValue(recordImplementation)
            ?? throw new InvalidOperationException("RecordImplementation.Session is null");
        _pSessionDatabase ??= AlRunner.Infrastructure.BcShape.Property(
            session.GetType(), "Database", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, CompanyStoreSurface);
        var database = _pSessionDatabase.GetValue(session)
            ?? throw new InvalidOperationException("NavSession.Database is null");
        _pDatabaseCompanyTokens ??= AlRunner.Infrastructure.BcShape.Property(
            database.GetType(), "CompanyTokens", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, CompanyStoreSurface);
        var tokens = _pDatabaseCompanyTokens.GetValue(database)
            ?? throw new InvalidOperationException("NavDatabase.CompanyTokens is null");
        _mCompanyTokensGetByToken ??= AlRunner.Infrastructure.BcShape.RequiredMethod(
            tokens.GetType(), "Get", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            CompanyStoreSurface, "CompanyTokens.Get(int)",
            "a clone of a record on another company could not name its company",
            new[] { typeof(int) });
        return (string)_mCompanyTokensGetByToken.Invoke(tokens, new object[] { token })!;
    }

    /// <summary>
    /// The store a record on a non-session company reads, or <paramref name="sessionStore"/> when the
    /// record is on the session company or its table is not one a company can own rows of.
    /// </summary>
    private static object RouteToCompanyStore(object recordImplementation, object sessionStore)
    {
        var token = RecordCompanyToken(recordImplementation);
        if (token == 0) return sessionStore;
        if (_companyStores.TryGetValue(sessionStore, out var known) && known.TryGetValue(token, out var sibling))
            return sibling;
        if (_notCompanyRouted.TryGetValue(sessionStore, out _)) return sessionStore;

        _fRecImplMetaTableForCompany ??= RequiredField(recordImplementation.GetType(), "metaTable", CompanyStoreSurface);
        var meta = (NCLMetaTable?)_fRecImplMetaTableForCompany.GetValue(recordImplementation);
        return meta == null ? sessionStore : CompanyStoreFor(sessionStore, meta, token);
    }

    /// <summary>
    /// The store of company <paramref name="token"/> shadowing <paramref name="sessionStore"/>, created
    /// empty on first use. <paramref name="sessionStore"/> when the table is not per company, is
    /// temporary, is a system or virtual table, or is not a store <c>_dataAccessByTable</c> handed out.
    /// </summary>
    private static object CompanyStoreFor(object sessionStore, NCLMetaTable meta, int token)
    {
        if (token == 0) return sessionStore;
        if (_companyStores.TryGetValue(sessionStore, out var known) && known.TryGetValue(token, out var existing))
            return existing;
        if (_notCompanyRouted.TryGetValue(sessionStore, out _)) return sessionStore;

        var source = CompanyRoutedSource(sessionStore, meta);
        if (source == null)
        {
            _notCompanyRouted.AddOrUpdate(sessionStore, _temporaryRecordSentinel);
            return sessionStore;
        }

        var stores = _companyStores.GetValue(sessionStore, static _ => new ConcurrentDictionary<int, object>());
        return stores.GetOrAdd(token, _ =>
        {
            var created = _mCreateTempDataAccess!.Invoke(source, new object[] { meta })!;
            // The same registration GetDataAccessForTable gives a permanent table's store: BLOB writes
            // and row-version stamping treat it as database-backed, not as a `temporary` record's.
            BlobStoreIsolationPatches.MarkDatabaseBacked(created);
            return created;
        });
    }

    /// <summary>
    /// The DataAccessSource whose per-table cache holds <paramref name="sessionStore"/> for
    /// <paramref name="meta"/>, when that store is a permanent per-company table's; else null.
    /// </summary>
    private static object? CompanyRoutedSource(object sessionStore, NCLMetaTable meta)
    {
        if (!meta.DataPerCompany || meta.TableId >= SystemTableIdFloor) return null;
        if (_temporaryRecordDataAccess.TryGetValue(sessionStore, out _)) return null;
        if (TableConnectionPatches.IsExternalTableType(meta, out _)) return null;
        foreach (var (source, perTable) in _dataAccessByTable)
            if (perTable.TryGetValue(meta.TableId, out var live) && ReferenceEquals(live, sessionStore))
                return source;
        return null;
    }

    private const int SystemTableIdFloor = 2000000000;

    /// <summary>
    /// <c>DataAccessSource.GetDataAccessForTable</c> for a caller that has no record but does know the
    /// company token it is working for (FlowField source tables). Token 0 is the plain handout.
    /// </summary>
    public static object GetDataAccessForTableInCompany(object source, NCLMetaTable table, int companyToken)
    {
        var sessionStore = NavDataAccessSource_GetDataAccessForTable(source, table, false);
        return companyToken == 0 ? sessionStore : CompanyStoreFor(sessionStore, table, companyToken);
    }

    /// <summary>
    /// The store a rollback snapshot of (<paramref name="source"/>, <paramref name="tableId"/>,
    /// <paramref name="token"/>) reads and restores. Token 0 is the session company's. With
    /// <paramref name="create"/> false a company whose store was never created answers null.
    /// </summary>
    private static object? SnapshotStore(object source, int tableId, int token, bool create)
    {
        if (!_dataAccessByTable.TryGetValue(source, out var perTable)) return null;
        if (!perTable.TryGetValue(tableId, out var sessionStore)) return null;
        if (token == 0) return sessionStore;
        if (_companyStores.TryGetValue(sessionStore, out var known) && known.TryGetValue(token, out var sibling))
            return sibling;
        if (!create) return null;
        var meta = EnsureTableInMetadataCache(tableId);
        if (meta == null) return null;
        var routed = CompanyStoreFor(sessionStore, meta, token);
        return ReferenceEquals(routed, sessionStore) ? null : routed;
    }
}
