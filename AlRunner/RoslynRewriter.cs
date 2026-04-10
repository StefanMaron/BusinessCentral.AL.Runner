using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

// No namespace - must be accessible from Program.cs top-level statements

/// <summary>
/// Roslyn CSharpSyntaxRewriter that transforms BC-generated C# into standalone code.
/// Replaces the fragile regex-based CSharpRewriter (now RegexRewriter) with a proper
/// syntax-tree-based approach.
/// </summary>
public class RoslynRewriter : CSharpSyntaxRewriter
{
    private static readonly HashSet<string> BcAttributeNames = new(StringComparer.Ordinal)
    {
        "NavCodeunitOptions",
        "NavFunctionVisibility",
        "NavCaption",
        "NavName",
        // "NavTest" — kept for test method discovery by the executor
        "SignatureSpan",
        "SourceSpans",
        "ReturnValue",
        "NavObjectId",
        "NavByReferenceAttribute",
    };

    private static readonly HashSet<string> RemoveUsings = new(StringComparer.Ordinal)
    {
        "Microsoft.Dynamics.Nav.Runtime.Extensions",
        "Microsoft.Dynamics.Nav.Runtime.Report",
        "Microsoft.Dynamics.Nav.EventSubscription",
        "Microsoft.Dynamics.Nav.Common.Language",
    };

    /// <summary>
    /// Methods that take ITreeObject as first arg which we strip (e.g., value.ALByValue(this))
    /// </summary>
    // Methods on BC types that accept ITreeObject/NavRecord but should be no-ops in standalone mode
    private static readonly HashSet<string> StripEntireCallMethods = new(StringComparer.Ordinal)
    {
        "ALGetTable", // NavRecordRef.ALGetTable(NavRecord) — record assertion methods only
        "ALClose",    // NavRecordRef.ALClose()
        "RunEvent",   // NavEventScope.RunEvent() — event subscriber dispatch, no-op standalone
        "ALCommit",   // ALDatabase.ALCommit() — SQL transaction commit, no-op standalone
        "ALCommit",   // ALDatabase.ALCommit() — transaction commit, no-op standalone
        "ALSelectLatestVersion", // ALDatabase.ALSelectLatestVersion() — no-op standalone
        "ALBindSubscription",   // ALSession.ALBindSubscription() — event binding, no-op standalone
        "ALUnbindSubscription", // ALSession.ALUnbindSubscription() — event unbinding, no-op standalone
    };

    private static readonly HashSet<string> StripITreeObjectArgMethods = new(StringComparer.Ordinal)
    {
        "ALByValue", "ModifyLength", "ALRecord",
    };

    /// <summary>
    /// Methods where the first 'this' argument (ITreeObject) should be replaced with null!,
    /// keeping the rest of the call intact. These are BC runtime methods on real BC types
    /// that require ITreeObject but work with null in standalone mode.
    /// e.g. blob.ALCreateInStream(this, inStr) -> blob.ALCreateInStream(null!, inStr)
    /// </summary>
    private static readonly HashSet<string> NullifyFirstThisArgMethods = new(StringComparer.Ordinal)
    {
        "ALCreateInStream", "ALCreateOutStream",
    };

    /// <summary>
    /// Methods where the first 'this' argument (ITreeObject) should be removed entirely,
    /// keeping the rest of the call intact. These are methods on mock types (MockRecordRef)
    /// where the mock doesn't need the ITreeObject parameter.
    /// e.g. recRef.ALField(this, fieldNo) -> recRef.ALField(fieldNo)
    /// </summary>
    private static readonly HashSet<string> StripFirstThisArgMethods = new(StringComparer.Ordinal)
    {
        "ALField",
    };

    /// <summary>
    /// Names of .Target methods on record handles that should have .Target stripped.
    /// </summary>
    private static readonly HashSet<string> RecordTargetMethods = new(StringComparer.Ordinal)
    {
        "ALInit", "ALInsert", "ALModify", "ALGet", "ALFind", "ALNext", "ALDelete",
        "ALDeleteAll", "ALCount", "ALSetRange", "ALSetFilter", "ALFindSet",
        "ALFindFirst", "ALFindLast", "ALIsEmpty", "ALCalcFields", "ALSetCurrentKey",
        "ALReset", "ALCopy", "ALCopyFilter", "ALCopyFilters", "ALTestField", "ALTestFieldSafe", "ALValidate", "ALValidateSafe", "ALRename",
        "ALLockTable", "ALCalcSums", "ALSetLoadFields", "ALFieldCaption", "ALSetRecFilter",
        "ALTableCaption", "ALTableName", "ALTestFieldNavValueSafe",
        "ALFilterGroup", "ALSetRangeSafe", "ALReadIsolation",
        "ALTransferFields", "ALMark", "ALMarkedOnly",
        "ALGetFilters", "ALGetRangeMinSafe", "ALGetRangeMaxSafe",
        "SetFieldValueSafe", "GetFieldValueSafe", "GetFieldRefSafe",
    };

    public static string Rewrite(string csharp)
    {
        var tree = CSharpSyntaxTree.ParseText(csharp);
        var root = tree.GetRoot();

        var rewriter = new RoslynRewriter();

        var newRoot = rewriter.Visit(root);
        return newRoot.NormalizeWhitespace().ToFullString();
    }

    // -----------------------------------------------------------------------
    // Using directives
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitUsingDirective(UsingDirectiveSyntax node)
    {
        var name = node.NamespaceOrType.ToString();
        if (RemoveUsings.Contains(name))
            return null;
        return base.VisitUsingDirective(node);
    }

    // -----------------------------------------------------------------------
    // Namespace: inject "using AlRunner.Runtime;" after opening brace
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitNamespaceDeclaration(NamespaceDeclarationSyntax node)
    {
        // First recurse into children
        var visited = (NamespaceDeclarationSyntax)base.VisitNamespaceDeclaration(node)!;

        // Add using AlRunner.Runtime if not already present
        bool hasRuntimeUsing = visited.Usings.Any(u =>
            u.NamespaceOrType.ToString() == "AlRunner.Runtime");

        if (!hasRuntimeUsing)
        {
            var usingDirective = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("AlRunner.Runtime"));
            visited = visited.AddUsings(usingDirective);
        }

        return visited;
    }

    // Also handle file-scoped namespaces
    public override SyntaxNode? VisitFileScopedNamespaceDeclaration(FileScopedNamespaceDeclarationSyntax node)
    {
        var visited = (FileScopedNamespaceDeclarationSyntax)base.VisitFileScopedNamespaceDeclaration(node)!;

        bool hasRuntimeUsing = visited.Usings.Any(u =>
            u.NamespaceOrType.ToString() == "AlRunner.Runtime");

        if (!hasRuntimeUsing)
        {
            var usingDirective = SyntaxFactory.UsingDirective(
                SyntaxFactory.ParseName("AlRunner.Runtime"));
            visited = visited.AddUsings(usingDirective);
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Attribute lists: remove BC-specific attributes
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitAttributeList(AttributeListSyntax node)
    {
        var kept = new SeparatedSyntaxList<AttributeSyntax>();
        foreach (var attr in node.Attributes)
        {
            var attrName = GetSimpleAttributeName(attr);
            if (!BcAttributeNames.Contains(attrName))
                kept = kept.Add(attr);
        }

        if (kept.Count == 0)
            return null; // remove entire attribute list

        if (kept.Count == node.Attributes.Count)
            return base.VisitAttributeList(node); // nothing changed

        return node.WithAttributes(kept);
    }

    private static string GetSimpleAttributeName(AttributeSyntax attr)
    {
        // Extract the last identifier from potentially qualified name
        var name = attr.Name;
        if (name is QualifiedNameSyntax qns)
            return qns.Right.Identifier.Text;
        if (name is IdentifierNameSyntax ins)
            return ins.Identifier.Text;
        return name.ToString();
    }

    // -----------------------------------------------------------------------
    // Class declarations: handle base classes, remove BC members, add _parent field
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitClassDeclaration(ClassDeclarationSyntax node)
    {
        // Detect if this is a scope class BEFORE visiting children.
        // We need to know the enclosing class name for _parent field type.
        bool isScopeClass = false;
        bool isRecordClass = false;
        bool isPageClass = false;
        string? enclosingClassName = null;
        if (node.BaseList != null)
        {
            foreach (var baseType in node.BaseList.Types)
            {
                var typeText = baseType.Type.ToString();
                if (typeText == "NavRecord" || typeText == "NavRecordExtension")
                    isRecordClass = true;
                if (typeText == "NavForm" || typeText == "NavFormExtension")
                    isPageClass = true;
                if (typeText.StartsWith("NavMethodScope<") || typeText.StartsWith("NavTriggerMethodScope<")
                    || typeText.StartsWith("NavEventMethodScope<"))
                {
                    isScopeClass = true;
                    // Extract the generic type parameter as the enclosing class name
                    // NavMethodScope<Codeunit139771> -> Codeunit139771
                    var ltIdx = typeText.IndexOf('<');
                    var gtIdx = typeText.IndexOf('>');
                    if (ltIdx >= 0 && gtIdx > ltIdx)
                        enclosingClassName = typeText.Substring(ltIdx + 1, gtIdx - ltIdx - 1);
                    break;
                }
            }
        }

        // First, visit children recursively
        var visited = (ClassDeclarationSyntax)base.VisitClassDeclaration(node)!;

        // Handle base class list
        if (visited.BaseList != null)
        {
            var newTypes = new SeparatedSyntaxList<BaseTypeSyntax>();
            foreach (var baseType in visited.BaseList.Types)
            {
                var typeText = baseType.Type.ToString();

                if (typeText == "NavCodeunit" || typeText == "NavTestCodeunit" || typeText == "NavRecord"
                    || typeText == "NavFormExtension" || typeText == "NavRecordExtension"
                    || typeText == "NavEventScope" || typeText == "NavUpgradeCodeunit"
                    || typeText == "NavForm")
                {
                    // Remove these base classes entirely
                    continue;
                }

                if (typeText.StartsWith("NavMethodScope<") || typeText.StartsWith("NavTriggerMethodScope<")
                    || typeText.StartsWith("NavEventMethodScope<"))
                {
                    // Replace with AlScope
                    var alScopeType = SyntaxFactory.SimpleBaseType(
                        SyntaxFactory.ParseTypeName("AlScope"));
                    newTypes = newTypes.Add(alScopeType);
                    continue;
                }

                newTypes = newTypes.Add(baseType);
            }

            if (newTypes.Count == 0)
                visited = visited.WithBaseList(null);
            else
                visited = visited.WithBaseList(SyntaxFactory.BaseList(newTypes));
        }

        // Remove specific members
        var membersToKeep = new SyntaxList<MemberDeclarationSyntax>();
        foreach (var member in visited.Members)
        {
            if (ShouldRemoveMember(member, visited))
                continue;
            membersToKeep = membersToKeep.Add(member);
        }

        visited = visited.WithMembers(membersToKeep);

        // For scope classes: add a _parent field of the enclosing class type
        if (isScopeClass && enclosingClassName != null)
        {
            var parentField = SyntaxFactory.FieldDeclaration(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.ParseTypeName(enclosingClassName))
                .WithVariables(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator("_parent"))))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PrivateKeyword)));

            // Insert the _parent field at the beginning
            visited = visited.WithMembers(visited.Members.Insert(0, parentField));
        }

        // For Record classes: add delegating methods that forward to Rec (MockRecordHandle).
        // In the original code, Record2632 : NavRecord, so it inherits GetFieldValueSafe, ALModify, etc.
        // After rewriting, the base class is removed and Rec is a separate MockRecordHandle property.
        // Scope classes reference _parent.GetFieldValueSafe(...) which needs to work on the Record class.
        if (isRecordClass)
        {
            var delegatingCode = @"
public NavValue GetFieldValueSafe(int fieldNo, NavType expectedType) => Rec.GetFieldValueSafe(fieldNo, expectedType);
public NavValue GetFieldValueSafe(int fieldNo, NavType expectedType, bool useLocale) => Rec.GetFieldValueSafe(fieldNo, expectedType, useLocale);
public void SetFieldValueSafe(int fieldNo, NavType expectedType, NavValue value) => Rec.SetFieldValueSafe(fieldNo, expectedType, value);
public void SetFieldValueSafe(int fieldNo, NavType expectedType, NavValue value, bool validate) => Rec.SetFieldValueSafe(fieldNo, expectedType, value, validate);
public NavValue GetFieldRefSafe(int fieldNo, NavType expectedType) => Rec.GetFieldRefSafe(fieldNo, expectedType);
public bool ALInsert(DataError errorLevel) => Rec.ALInsert(errorLevel);
public bool ALInsert(DataError errorLevel, bool runTrigger) => Rec.ALInsert(errorLevel, runTrigger);
public bool ALModify(DataError errorLevel) => Rec.ALModify(errorLevel);
public bool ALModify(DataError errorLevel, bool runTrigger) => Rec.ALModify(errorLevel, runTrigger);
public bool ALGet(DataError errorLevel, params NavValue[] keyValues) => Rec.ALGet(errorLevel, keyValues);
public bool ALFind(DataError errorLevel, string searchMethod = ""-"") => Rec.ALFind(errorLevel, searchMethod);
public bool ALFindSet(DataError errorLevel = DataError.ThrowError, bool forUpdate = false) => Rec.ALFindSet(errorLevel, forUpdate);
public bool ALFindFirst(DataError errorLevel = DataError.ThrowError) => Rec.ALFindFirst(errorLevel);
public bool ALFindLast(DataError errorLevel = DataError.ThrowError) => Rec.ALFindLast(errorLevel);
public int ALNext() => Rec.ALNext();
public bool ALDelete(DataError errorLevel, bool runTrigger = false) => Rec.ALDelete(errorLevel, runTrigger);
public void ALDeleteAll(bool runTrigger) => Rec.ALDeleteAll(runTrigger);
public void ALDeleteAll(DataError errorLevel = DataError.ThrowError, bool runTrigger = false) => Rec.ALDeleteAll(errorLevel, runTrigger);
public void ALInit() => Rec.ALInit();
public void ALReset() => Rec.ALReset();
public void ALSetRange(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue) => Rec.ALSetRange(fieldNo, expectedType, fromValue, toValue);
public void ALSetRangeSafe(int fieldNo, NavType expectedType) => Rec.ALSetRangeSafe(fieldNo, expectedType);
public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue value) => Rec.ALSetRangeSafe(fieldNo, expectedType, value);
public void ALSetRangeSafe(int fieldNo, NavType expectedType, NavValue fromValue, NavValue toValue) => Rec.ALSetRangeSafe(fieldNo, expectedType, fromValue, toValue);
public void ALSetFilter(int fieldNo, string filterExpression, params NavValue[] args) => Rec.ALSetFilter(fieldNo, filterExpression, args);
public void ALSetFilter(int fieldNo, NavType expectedType, string filterExpression, params NavValue[] args) => Rec.ALSetFilter(fieldNo, expectedType, filterExpression, args);
public void ALCopy(MockRecordHandle source, bool shareFilters = false) => Rec.ALCopy(source, shareFilters);
public void ALCopyFilter(int fromFieldNo, MockRecordHandle target, int toFieldNo) => Rec.ALCopyFilter(fromFieldNo, target, toFieldNo);
public void ALCopyFilters(MockRecordHandle source) => Rec.ALCopyFilters(source);
public void ALValidateSafe(int fieldNo, NavType expectedType, NavValue value) => Rec.ALValidateSafe(fieldNo, expectedType, value);
public void ALValidate(DataError errorLevel, int fieldNo, NavType expectedType, NavValue value) => Rec.ALValidate(errorLevel, fieldNo, expectedType, value);
public void ALTestFieldSafe(int fieldNo, NavType expectedType) => Rec.ALTestFieldSafe(fieldNo, expectedType);
public void ALTestFieldSafe(int fieldNo, NavType expectedType, NavValue expectedValue) => Rec.ALTestFieldSafe(fieldNo, expectedType, expectedValue);
public void ALTestField(DataError errorLevel, int fieldNo, NavType expectedType) => Rec.ALTestField(errorLevel, fieldNo, expectedType);
public void ALTestField(DataError errorLevel, int fieldNo, NavType expectedType, NavValue expectedValue) => Rec.ALTestField(errorLevel, fieldNo, expectedType, expectedValue);
public void ALCalcFields(DataError errorLevel, params int[] fieldNos) => Rec.ALCalcFields(errorLevel, fieldNos);
public bool ALCalcSums(DataError errorLevel, params int[] fieldNos) => Rec.ALCalcSums(errorLevel, fieldNos);
public void ALSetCurrentKey(DataError errorLevel, params int[] fieldNos) => Rec.ALSetCurrentKey(errorLevel, fieldNos);
public void ALSetCurrentKey(params int[] fieldNos) => Rec.ALSetCurrentKey(fieldNos);
public void ALSetAscending(int fieldNo, bool ascending) => Rec.ALSetAscending(fieldNo, ascending);
public int ALCount => Rec.ALCount;
public bool ALIsEmpty => Rec.ALIsEmpty;
public bool ALIsTemporary => Rec.ALIsTemporary;
public string ALTableCaption => Rec.ALTableCaption;
public string ALTableName => Rec.ALTableName;
public int ALFieldNo(string fieldName) => Rec.ALFieldNo(fieldName);
public int ALFieldNo(int fieldNo) => Rec.ALFieldNo(fieldNo);
public NavText ALFieldCaption(int fieldNo) => Rec.ALFieldCaption(fieldNo);
public void ALSetRecFilter() => Rec.ALSetRecFilter();
public void ALLockTable(DataError errorLevel = DataError.ThrowError) => Rec.ALLockTable(errorLevel);
public bool ALRename(DataError errorLevel, params NavValue[] newKeyValues) => Rec.ALRename(errorLevel, newKeyValues);
public void ALSetLoadFields(params int[] fieldNos) => Rec.ALSetLoadFields(fieldNos);
public void ALSetAutoCalcFields(params object[] fields) => Rec.ALSetAutoCalcFields(fields);
public string ALGetFilter() => Rec.ALGetFilter();
public string ALGetFilter(int fieldNo) => Rec.ALGetFilter(fieldNo);
public void ALAssign(MockRecordHandle other) => Rec.ALAssign(other);
public int ALFilterGroup { get => Rec.ALFilterGroup; set => Rec.ALFilterGroup = value; }
public object ALReadIsolation { get => Rec.ALReadIsolation; set => Rec.ALReadIsolation = value; }
public void Clear() => Rec.Clear();
public void ALTransferFields(MockRecordHandle source, bool initPrimaryKey = true) => Rec.ALTransferFields(source, initPrimaryKey);
public void ALMark(bool mark = true) => Rec.ALMark(mark);
public bool ALMarkedOnly { get => Rec.ALMarkedOnly; set => Rec.ALMarkedOnly = value; }
public int CurrFieldNo { get; set; }
public string ALGetFilters() => Rec.ALGetFilters();
public NavValue ALGetRangeMinSafe(int fieldNo, NavType expectedType) => Rec.ALGetRangeMinSafe(fieldNo, expectedType);
public NavValue ALGetRangeMaxSafe(int fieldNo, NavType expectedType) => Rec.ALGetRangeMaxSafe(fieldNo, expectedType);
";
            var delegatingMembers = CSharpSyntaxTree.ParseText(
                $"class _Temp_ {{ {delegatingCode} }}").GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members;

            visited = visited.WithMembers(visited.Members.AddRange(delegatingMembers));
        }

        // For Page classes: add SaveRecord/Update/SetSelectionFilter/CheckType stubs
        if (isPageClass)
        {
            var pageStubCode = @"
public void SaveRecord() { }
public void Update(bool saveRecord) { }
public void SetSelectionFilter(MockRecordHandle record) { }
public void CheckType(NavType actual, NavType expected) { }
";
            var pageMembers = CSharpSyntaxTree.ParseText(
                $"class _Temp_ {{ {pageStubCode} }}").GetRoot()
                .DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members;

            visited = visited.WithMembers(visited.Members.AddRange(pageMembers));
        }

        return visited;
    }

    private static bool ShouldRemoveMember(MemberDeclarationSyntax member, ClassDeclarationSyntax parentClass)
    {
        // Remove BC constructor: public CodeunitXXX(ITreeObject parent) : base(parent, NNN) { }
        // Remove Record constructor: public RecordXXX(ITreeObject parent, NCLMetaTable ...) : base(...) { }
        if (member is ConstructorDeclarationSyntax ctor)
        {
            var paramText = ctor.ParameterList.ToString();
            if (paramText.Contains("ITreeObject parent"))
                return true;
        }

        // Remove methods
        if (member is MethodDeclarationSyntax method)
        {
            var name = method.Identifier.Text;

            // Remove __Construct
            if (name == "__Construct")
                return true;

            // Remove OnInvoke
            if (name == "OnInvoke" && method.ParameterList.Parameters.Count == 2)
            {
                var firstParam = method.ParameterList.Parameters[0].Type?.ToString();
                var secondParam = method.ParameterList.Parameters[1].Type?.ToString();
                if (firstParam == "int" && secondParam == "object[]")
                    return true;
            }

            // Remove OnRun with parameters (the codeunit's OnRun, not the scope's)
            if (name == "OnRun" && method.ParameterList.Parameters.Count > 0)
                return true;

            // Remove GetMethodScopeFlags (BC runtime permission checking, irrelevant for standalone)
            if (name == "GetMethodScopeFlags")
                return true;

            // Remove BC interface dispatch methods (used by NavInterfaceHandle runtime)
            if (name == "IsInterfaceOfType" || name == "IsInterfaceMethod")
                return true;

            // Remove Page/Extension-specific methods that reference BC Page runtime
            if (name == "OnMetadataLoaded" || name == "EvaluateCaptionClass"
                || name == "OnEvaluateCaptionClass" || name == "RegisterDynamicCaptionExpression"
                || name == "EnsureGlobalVariablesInitialized" || name == "CallEvaluateCaptionClassExtensionMethod"
                || name == "CallOnMetadataLoadedExtensionMethod"
                || name == "RegisterUIPart")
                return true;

            // Remove Page InitializeComponent that contains NavForm-specific calls.
            // Codeunit InitializeComponent doesn't call these, so this is safe.
            if (name == "InitializeComponent" || name == "InitializeForm")
            {
                var bodyText = method.Body?.ToString() ?? "";
                if (bodyText.Contains("CallInitializeComponentExtensionMethod") ||
                    bodyText.Contains("InitializeForm") ||
                    bodyText.Contains("RegisterUIPart"))
                    return true;
            }
        }

        // Remove specific properties
        if (member is PropertyDeclarationSyntax prop)
        {
            var name = prop.Identifier.Text;

            // public override string ObjectName => "...";
            if (name == "ObjectName")
                return true;

            // public override bool IsCompiledForOnPremise => true;
            if (name == "IsCompiledForOnPremise")
                return true;

            // public override bool IsSingleInstance => false;
            if (name == "IsSingleInstance")
                return true;

            // Rec/xRec: Don't remove — rewrite to MockRecordHandle stub in VisitPropertyDeclaration
            // (removed the deletion that was here)

            // protected override uint RawScopeId { get => ...; set => ...; }
            if (name == "RawScopeId")
                return true;

            // private new NavRecord ParentObject => ...;
            if (name == "ParentObject")
                return true;
            // CurrPage: Don't remove — rewrite to MockFormHandle in VisitPropertyDeclaration

            // protected override uint[] IndirectPermissionList => ...;
            if (name == "IndirectPermissionList")
                return true;

            // public override NavEventScope EventScope { get; set; }
            if (name == "EventScope")
                return true;

            // public override int MethodId => N;
            if (name == "MethodId")
                return true;
        }

        // Remove static αscopeId field (Unicode \u03b1 prefix)
        if (member is FieldDeclarationSyntax field)
        {
            foreach (var variable in field.Declaration.Variables)
            {
                var name = variable.Identifier.ValueText;
                var text = variable.Identifier.Text;
                // Match by ValueText, Text, or fallback pattern: any field ending with "scopeId"
                if (name == "\u03b1scopeId" || text == "\u03b1scopeId" ||
                    name.EndsWith("scopeId") || text.EndsWith("scopeId"))
                    return true;
            }
        }

        return false;
    }

    // -----------------------------------------------------------------------
    // Method declarations: remove 'override' from OnClear
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node)
    {
        var visited = (MethodDeclarationSyntax)base.VisitMethodDeclaration(node)!;

        // Remove 'override' keyword from methods whose base class was removed.
        // We strip NavCodeunit/NavRecord/NavFormExtension/NavRecordExtension/NavEventScope,
        // so any 'override' in those classes becomes invalid.
        if (visited.Modifiers.Any(m => m.IsKind(SyntaxKind.OverrideKeyword)))
        {
            var parentClass = node.Parent as ClassDeclarationSyntax;
            if (parentClass?.BaseList != null)
            {
                bool hadRemovedBase = parentClass.BaseList.Types.Any(t =>
                {
                    var txt = t.Type.ToString();
                    return txt == "NavCodeunit" || txt == "NavTestCodeunit" || txt == "NavRecord"
                        || txt == "NavFormExtension" || txt == "NavRecordExtension"
                        || txt == "NavEventScope" || txt == "NavUpgradeCodeunit"
                        || txt == "NavForm";
                });
                if (hadRemovedBase)
                {
                    var newModifiers = SyntaxFactory.TokenList(
                        visited.Modifiers.Where(m => !m.IsKind(SyntaxKind.OverrideKeyword)));
                    visited = visited.WithModifiers(newModifiers);
                }
            }
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Property declarations: rewrite Rec/xRec to MockRecordHandle stubs
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
    {
        var name = node.Identifier.Text;

        // Rewrite Rec/xRec properties to return a MockRecordHandle(0) stub.
        // Original: private NavRecord Rec => (NavRecord)this.SourceTable;
        // Original: private RecordXXX Rec => (RecordXXX)this;
        // Rewritten: public MockRecordHandle Rec { get; } = new MockRecordHandle(0);
        if (name == "Rec" || name == "xRec")
        {
            // Parse a simple auto-property with a default value
            var stubProp = SyntaxFactory.PropertyDeclaration(
                    SyntaxFactory.ParseTypeName("MockRecordHandle"),
                    SyntaxFactory.Identifier(name))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithAccessorList(SyntaxFactory.AccessorList(
                    SyntaxFactory.SingletonList(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))))
                .WithInitializer(SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.ParseExpression("new MockRecordHandle(0)")))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            return stubProp;
        }

        // Rewrite CurrPage to return a MockFormHandle(0) stub.
        // Original: private PageXXX CurrPage => (PageXXX)this;
        // Rewritten: public MockFormHandle CurrPage { get; } = new MockFormHandle(0);
        if (name == "CurrPage")
        {
            var stubProp = SyntaxFactory.PropertyDeclaration(
                    SyntaxFactory.ParseTypeName("MockFormHandle"),
                    SyntaxFactory.Identifier("CurrPage"))
                .WithModifiers(SyntaxFactory.TokenList(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword)))
                .WithAccessorList(SyntaxFactory.AccessorList(
                    SyntaxFactory.SingletonList(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken)))))
                .WithInitializer(SyntaxFactory.EqualsValueClause(
                    SyntaxFactory.ParseExpression("new MockFormHandle(0)")))
                .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
            return stubProp;
        }

        return base.VisitPropertyDeclaration(node);
    }

    // -----------------------------------------------------------------------
    // Constructor declarations: handle scope ctors with _parent field
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node)
    {
        var visited = (ConstructorDeclarationSyntax)base.VisitConstructorDeclaration(node)!;

        // For scope constructors (internal constructors in nested classes that inherit from AlScope),
        // the base class has been replaced with AlScope which has a parameterless constructor.
        // Remove ALL base(...) initializers from these constructors.
        if (visited.Initializer != null && visited.Initializer.Kind() == SyntaxKind.BaseConstructorInitializer)
        {
            // Check if this is a scope constructor by looking for parent or βparent references,
            // or simply any base() call on an internal constructor (scope constructors are internal).
            var isInternal = visited.Modifiers.Any(m => m.IsKind(SyntaxKind.InternalKeyword));
            var hasParentArg = visited.Initializer.ArgumentList.Arguments
                .Any(a => a.Expression is IdentifierNameSyntax id &&
                    (id.Identifier.ValueText.Contains("parent") || id.Identifier.Text.Contains("parent")));

            if (hasParentArg || isInternal)
            {
                visited = visited.WithInitializer(null);
            }
        }

        // Handle βparent parameter: KEEP it but add _parent assignment to constructor body.
        // Instead of removing the parameter, we keep it and add: this._parent = βparent;
        if (visited.ParameterList.Parameters.Count > 0)
        {
            var firstParam = visited.ParameterList.Parameters[0];
            var paramName = firstParam.Identifier.ValueText;
            if (paramName.Contains("parent") || firstParam.Identifier.Text.Contains("parent"))
            {
                var typeText = firstParam.Type?.ToString() ?? "";
                if (typeText.StartsWith("Codeunit") || typeText.StartsWith("Record") || typeText.StartsWith("Page")
                    || typeText.StartsWith("Query") || typeText.StartsWith("Report") || typeText.StartsWith("XmlPort"))
                {
                    // Keep the parameter, but add _parent assignment at the start of the body
                    var paramIdentifier = firstParam.Identifier.Text;
                    var assignmentStatement = SyntaxFactory.ParseStatement(
                        $"this._parent = {paramIdentifier};");

                    if (visited.Body != null)
                    {
                        var newStatements = visited.Body.Statements.Insert(0, assignmentStatement);
                        visited = visited.WithBody(visited.Body.WithStatements(newStatements));
                    }
                    else if (visited.ExpressionBody != null)
                    {
                        // Convert expression body to block body with assignment + expression
                        var exprStatement = SyntaxFactory.ExpressionStatement(visited.ExpressionBody.Expression);
                        var body = SyntaxFactory.Block(assignmentStatement, exprStatement);
                        visited = visited.WithExpressionBody(null)
                            .WithSemicolonToken(SyntaxFactory.MissingToken(SyntaxKind.SemicolonToken))
                            .WithBody(body);
                    }

                    // Do NOT remove the parameter - keep it so callers can pass 'this'
                }
            }
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Identifier names: type replacements
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitIdentifierName(IdentifierNameSyntax node)
    {
        var text = node.Identifier.Text;

        // INavRecordHandle -> MockRecordHandle
        if (text == "INavRecordHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockRecordHandle"));

        // NavRecordHandle -> MockRecordHandle (used in new NavRecordHandle(...))
        if (text == "NavRecordHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockRecordHandle"));

        // NavCodeunitHandle -> MockCodeunitHandle
        if (text == "NavCodeunitHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockCodeunitHandle"));

        // NavInterfaceHandle -> MockInterfaceHandle
        if (text == "NavInterfaceHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockInterfaceHandle"));

        // NavRecordRef -> MockRecordRef (NavRecordRef requires ITreeObject -> NavSession)
        if (text == "NavRecordRef")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockRecordRef"));

        // NavVariant -> MockVariant (Variant in AL needs Default/ALAssign methods)
        if (text == "NavVariant")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockVariant"));

        // NavTextConstant -> NavText (avoid BC runtime initialization)
        if (text == "NavTextConstant")
            return node.WithIdentifier(SyntaxFactory.Identifier("NavText"));

        // NavDialog (instance type) -> MockDialog (for dialog/progress window objects)
        // Note: NavDialog static calls (ALMessage, ALError) are handled in VisitInvocationExpression
        if (text == "NavDialog")
        {
            // Check if this is in a type context (field/variable declaration, generic arg, etc.)
            // vs a static member access (NavDialog.ALMessage) which should stay as "NavDialog"
            var parent = node.Parent;
            if (parent is MemberAccessExpressionSyntax ma && ma.Expression == node)
            {
                // NavDialog.ALMessage -- keep as-is, handled by invocation rewriter
                // But for NavDialog.ALError, NavDialog.ALMessage these go through AlDialog
                // Actually check: is this a static method call?
                var methodName = ma.Name.Identifier.Text;
                if (methodName == "ALMessage" || methodName == "ALError")
                    return base.VisitIdentifierName(node); // Keep NavDialog for static rewrite
            }
            return node.WithIdentifier(SyntaxFactory.Identifier("MockDialog"));
        }

        // NavEventScope -> object (event scope type used for static fields)
        if (text == "NavEventScope")
            return node.WithIdentifier(SyntaxFactory.Identifier("object"));

        // NavTestPageHandle -> MockTestPageHandle (avoid ambiguity with BC runtime type)
        if (text == "NavTestPageHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockTestPageHandle"));

        // NavTestFieldHandle -> MockTestFieldHandle
        if (text == "NavTestFieldHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockTestFieldHandle"));

        // NavTestActionHandle -> MockTestActionHandle
        if (text == "NavTestActionHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockTestActionHandle"));

        // NavFormHandle -> MockFormHandle (page handle requires ITreeObject -> NavSession)
        if (text == "NavFormHandle")
            return node.WithIdentifier(SyntaxFactory.Identifier("MockFormHandle"));

        return base.VisitIdentifierName(node);
    }

    // -----------------------------------------------------------------------
    // Generic names: NavArray<MockRecordHandle> -> MockRecordArray
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitGenericName(GenericNameSyntax node)
    {
        var visited = (GenericNameSyntax)base.VisitGenericName(node)!;

        // NavArray<MockRecordHandle> -> MockRecordArray
        // NavArray<INavRecordHandle> -> MockRecordArray (INavRecordHandle already rewritten to MockRecordHandle)
        if (visited.Identifier.Text == "NavArray" &&
            visited.TypeArgumentList.Arguments.Count == 1)
        {
            var typeArg = visited.TypeArgumentList.Arguments[0].ToString();
            if (typeArg == "MockRecordHandle")
            {
                return SyntaxFactory.IdentifierName("MockRecordArray");
            }

            // NavArray<T> -> MockArray<T> for ALL types
            // NavArray requires ITreeObject (validates parent != null even in the simple ctor).
            // Use our MockArray<T> which provides 0-based indexing without ITreeObject.
            return SyntaxFactory.GenericName(
                SyntaxFactory.Identifier("MockArray"),
                visited.TypeArgumentList);
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Object creation expressions
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitObjectCreationExpression(ObjectCreationExpressionSyntax node)
    {
        var visited = (ObjectCreationExpressionSyntax)base.VisitObjectCreationExpression(node)!;
        var typeText = visited.Type.ToString();

        // new NavRecordHandle(this, NNN, false, SecurityFiltering.XXX) -> new MockRecordHandle(NNN)
        // After identifier replacement, this is already MockRecordHandle
        if (typeText == "MockRecordHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count >= 4)
        {
            // The second argument is the table ID
            var tableIdArg = visited.ArgumentList.Arguments[1];
            var newArgs = SyntaxFactory.ArgumentList(
                SyntaxFactory.SingletonSeparatedList(
                    SyntaxFactory.Argument(tableIdArg.Expression)));
            return visited.WithArgumentList(newArgs);
        }

        // new MockCodeunitHandle(this, NNN) -> MockCodeunitHandle.Create(NNN)
        if (typeText == "MockCodeunitHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 2)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                var codeunitId = visited.ArgumentList.Arguments[1].Expression;
                return SyntaxFactory.InvocationExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("MockCodeunitHandle"),
                        SyntaxFactory.IdentifierName("Create")),
                    SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(codeunitId))));
            }
        }

        // new MockInterfaceHandle(this) -> new MockInterfaceHandle()
        // Strip the 'this' arg since MockInterfaceHandle doesn't need ITreeObject
        if (typeText == "MockInterfaceHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 1)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockRecordRef(this, SecurityFiltering.XXX) -> new MockRecordRef()
        // After identifier replacement, NavRecordRef is now MockRecordRef.
        // Strip all arguments (ITreeObject and SecurityFiltering) since MockRecordRef is parameterless.
        if (typeText == "MockRecordRef" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count >= 1)
        {
            return visited.WithArgumentList(SyntaxFactory.ArgumentList());
        }

        // new MockDialog(this) -> new MockDialog()
        // After identifier replacement, NavDialog is now MockDialog.
        // Strip the ITreeObject 'this' argument.
        if (typeText == "MockDialog" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 1)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                return visited.WithArgumentList(SyntaxFactory.ArgumentList());
            }
        }

        // new MockFormHandle(this, pageId) -> new MockFormHandle(pageId)
        // After identifier replacement, NavFormHandle is now MockFormHandle.
        // Strip the ITreeObject 'this' argument.
        if (typeText == "MockFormHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 2)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                var pageId = visited.ArgumentList.Arguments[1].Expression;
                return visited.WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(pageId))));
            }
        }

        // new NavCode(maxLen, value) -> AlCompat.CreateNavCode(maxLen, value)
        // NavCode constructor calls EnsureValueIsUppercasedIfNeeded() which triggers NavEnvironment on Linux.
        // AlCompat.CreateNavCode pre-uppercases the string to avoid NavEnvironment access.
        if (typeText == "NavCode" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 2)
        {
            var maxLenArg = visited.ArgumentList.Arguments[0].Expression;
            var valueArg = visited.ArgumentList.Arguments[1].Expression;
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName("CreateNavCode")),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SeparatedList(new[] {
                        SyntaxFactory.Argument(maxLenArg),
                        SyntaxFactory.Argument(valueArg)
                    })));
        }

        // new NavTextConstant(langIds, strings, null, null) -> new NavText(strings[0])
        // After VisitIdentifierName, NavTextConstant is already renamed to NavText
        // NavTextConstant triggers NavEnvironment initialization; replace with simple NavText
        if ((typeText == "NavTextConstant" || typeText == "NavText") && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count >= 4)
        {
            // The second argument is the string array: new string[] { "the text" }
            var stringsArg = visited.ArgumentList.Arguments[1].Expression;
            if (stringsArg is ImplicitArrayCreationExpressionSyntax implArr && implArr.Initializer.Expressions.Count > 0)
            {
                return SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("NavText"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(implArr.Initializer.Expressions[0]))));
            }
            if (stringsArg is ArrayCreationExpressionSyntax arrCreate && arrCreate.Initializer != null && arrCreate.Initializer.Expressions.Count > 0)
            {
                return SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("NavText"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(
                            SyntaxFactory.Argument(arrCreate.Initializer.Expressions[0]))));
            }
        }

        // new NavTestPageHandle(this, pageId) -> new MockTestPageHandle(pageId)
        // After identifier replacement, the type is MockTestPageHandle
        // Strip the ITreeObject 'this' argument from constructor
        if (typeText == "MockTestPageHandle" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 2)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                var pageId = visited.ArgumentList.Arguments[1].Expression;
                return visited.WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(pageId))));
            }
        }

        // new NavFieldRef(this) -> new NavFieldRef(null!)
        // NavFieldRef constructor takes ITreeObject; replace 'this' with null! for standalone mode.
        if (typeText == "NavFieldRef" && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 1)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                var nullBang = SyntaxFactory.PostfixUnaryExpression(
                    SyntaxKind.SuppressNullableWarningExpression,
                    SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                return visited.WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(nullBang))));
            }
        }

        // new NavArray<MockRecordHandle>(new MockRecordHandle.Factory2(this, tableId, false, SecurityFiltering.X), N)
        // -> new MockRecordArray(tableId, N)
        // Also matches after GenericName rewrite: new MockRecordArray(new MockRecordHandle.Factory2(...), N)
        // NavArray<T> requires IFactory<T> which is internal; use our MockRecordArray instead
        if ((typeText.StartsWith("NavArray<") || typeText == "MockRecordArray") &&
            visited.ArgumentList != null && visited.ArgumentList.Arguments.Count == 2)
        {
            var factoryArg = visited.ArgumentList.Arguments[0].Expression;
            var sizeArg = visited.ArgumentList.Arguments[1].Expression;

            // Extract the table ID from the Factory2 constructor call
            if (factoryArg is ObjectCreationExpressionSyntax factoryCreation &&
                factoryCreation.Type.ToString().Contains("Factory2") &&
                factoryCreation.ArgumentList != null)
            {
                // Factory2(this, tableId, false, SecurityFiltering.X) — tableId is arg[1]
                // or Factory2(tableId, false, SecurityFiltering.X) — tableId is arg[0]
                var factoryArgs = factoryCreation.ArgumentList.Arguments;
                ExpressionSyntax? tableIdExpr = null;
                if (factoryArgs.Count >= 4)
                    tableIdExpr = factoryArgs[1].Expression; // (this, tableId, ...)
                else if (factoryArgs.Count >= 1)
                    tableIdExpr = factoryArgs[0].Expression; // (tableId, ...)

                if (tableIdExpr != null)
                {
                    return SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName("MockRecordArray"))
                        .WithArgumentList(SyntaxFactory.ArgumentList(
                            SyntaxFactory.SeparatedList(new[] {
                                SyntaxFactory.Argument(tableIdExpr),
                                SyntaxFactory.Argument(sizeArg)
                            })));
                }
            }
        }

        // new MockArray<MockVariant>(new NavVariant.Factory(...), N) -> new MockArray<MockVariant>(N, () => new MockVariant())
        // NavArray with Factory pattern: replace Factory-based construction with lambda-based MockArray
        if (typeText.StartsWith("MockArray<MockVariant>") && visited.ArgumentList != null &&
            visited.ArgumentList.Arguments.Count == 2)
        {
            var factoryArg = visited.ArgumentList.Arguments[0].Expression;
            var sizeArg = visited.ArgumentList.Arguments[1].Expression;
            if (factoryArg is ObjectCreationExpressionSyntax)
            {
                // new MockArray<MockVariant>(size, () => new MockVariant())
                return SyntaxFactory.ObjectCreationExpression(
                    SyntaxFactory.ParseTypeName("MockArray<MockVariant>"))
                    .WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(new[] {
                            SyntaxFactory.Argument(sizeArg),
                            SyntaxFactory.Argument(
                                SyntaxFactory.ParenthesizedLambdaExpression(
                                    SyntaxFactory.ObjectCreationExpression(
                                        SyntaxFactory.ParseTypeName("MockVariant"))
                                        .WithArgumentList(SyntaxFactory.ArgumentList())))
                        })));
            }
        }

        // Catch any remaining MockVariant.Factory or NavVariant.Factory construction
        if (typeText.Contains("MockVariant") && typeText.Contains("Factory") && visited.ArgumentList != null)
        {
            // This shouldn't be reached anymore but kept as safety net
            return SyntaxFactory.ObjectCreationExpression(
                SyntaxFactory.ParseTypeName("NavVariant.Factory"))
                .WithArgumentList(SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(
                            SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName("MockVariant"),
                                SyntaxFactory.IdentifierName("StubTreeObject"))))));
        }

        // new NavArray<T>(this, defaultValue, size) -> new MockArray<T>(defaultValue, size)
        // Drop the ITreeObject 'this' argument from NavArray/MockArray constructors.
        // After generic name rewrite, NavArray<T> becomes MockArray<T>.
        // MockArray<T>(T initValue, params int[] dimensions) matches the remaining args.
        if ((typeText.StartsWith("MockArray") || typeText.StartsWith("NavArray")) &&
            visited.ArgumentList != null && visited.ArgumentList.Arguments.Count >= 3)
        {
            var firstArgText = visited.ArgumentList.Arguments[0].Expression.ToString();
            if (firstArgText == "this")
            {
                var newArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                for (int i = 1; i < visited.ArgumentList.Arguments.Count; i++)
                    newArgs = newArgs.Add(visited.ArgumentList.Arguments[i]);
                return visited.WithArgumentList(SyntaxFactory.ArgumentList(newArgs));
            }
        }

        // NOTE: We no longer strip 'this' from scope constructor calls.
        // Scope constructors now keep the βparent parameter and store it as _parent.

        return visited;
    }

    // -----------------------------------------------------------------------
    // Invocation expressions: NavDialog, StmtHit, CStmtHit, NavRuntimeHelpers, ALCompiler
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitInvocationExpression(InvocationExpressionSyntax node)
    {
        // Check for StmtHit(N) and CStmtHit(N) BEFORE recursing into children
        if (node.Expression is IdentifierNameSyntax stmtIdent)
        {
            if (stmtIdent.Identifier.Text == "StmtHit")
            {
                // Will be removed at statement level (VisitExpressionStatement)
                // But if encountered as expression, return as-is for now
                return base.VisitInvocationExpression(node);
            }

            if (stmtIdent.Identifier.Text == "CStmtHit")
            {
                // Replace CStmtHit(N) with true
                return SyntaxFactory.LiteralExpression(SyntaxKind.TrueLiteralExpression);
            }
        }

        // Now recurse into children first
        var visited = (InvocationExpressionSyntax)base.VisitInvocationExpression(node)!;

        // NavDialog.ALMessage(this.Session, System.Guid.Parse("..."), fmt, args...)
        // -> AlDialog.Message(fmt, args...)
        if (visited.Expression is MemberAccessExpressionSyntax memberAccess)
        {
            var exprText = memberAccess.Expression.ToString();
            var methodName = memberAccess.Name.Identifier.Text;

            // NavDialog.ALConfirm(session, guid, question, [default]) -> MockDialog.ALConfirm(question, [default])
            if ((exprText == "NavDialog" || exprText == "MockDialog") && methodName == "ALConfirm")
            {
                var args = visited.ArgumentList.Arguments;
                // Skip first two args (Session and Guid)
                if (args.Count >= 3)
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 2; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);

                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("MockDialog"),
                            SyntaxFactory.IdentifierName("ALConfirm")),
                        SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // NavDialog.ALMessage / NavDialog.ALError
            if (exprText == "NavDialog" && (methodName == "ALMessage" || methodName == "ALError"))
            {
                var newMethodName = methodName == "ALMessage" ? "Message" : "Error";
                var args = visited.ArgumentList.Arguments;

                // Skip first two args (this.Session and System.Guid.Parse("..."))
                if (args.Count >= 2)
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 2; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);

                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlDialog"),
                            SyntaxFactory.IdentifierName(newMethodName)),
                        SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // NavRuntimeHelpers.CompilationError(...)
            if (exprText == "NavRuntimeHelpers" && methodName == "CompilationError")
            {
                // Replace with: throw new InvalidOperationException("Compilation error")
                // But this is an expression, we need to return an invocation.
                // The throw will be handled in VisitExpressionStatement.
                // Mark it for statement-level replacement by keeping it recognizable.
                return visited;
            }

            // NavCodeunit.RunCodeunit(DataError, id, record) -> MockCodeunitHandle.RunCodeunit(id)
            // NavCodeunit.RunCodeunit is a static dispatch method requiring NavSession
            if (exprText == "NavCodeunit" && methodName == "RunCodeunit")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 2)
                {
                    // The second argument is the codeunit ID
                    var codeunitIdArg = args[1].Expression;
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("MockCodeunitHandle"),
                            SyntaxFactory.IdentifierName("RunCodeunit")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(codeunitIdArg))));
                }
            }

            // NavForm.Run(pageId, record) -> no-op (page navigation not supported standalone)
            // NavForm.RunModal(bool, bool, pageId, record) -> FormResult.LookupOK
            if (exprText == "NavForm" && (methodName == "Run" || methodName == "RunModal"))
            {
                // RunModal returns FormResult, but the enum is from Nav.Ncl. Return a constant.
                // FormResult.LookupOK = used in transpiled code for dialog lookups
                // For Run (void), we can't return anything - the statement-level handler will remove it
                if (methodName == "RunModal")
                {
                    // Return 0 cast to the expected type (FormResult enum), or just return default
                    return SyntaxFactory.DefaultExpression(
                        SyntaxFactory.ParseTypeName("FormResult"));
                }
                // For Run: will be removed at statement level as a no-op
                return visited;
            }

            // NCLEnumMetadata.Create(N) -> NCLOptionMetadata.Default
            // NCLEnumMetadata.Create goes through NavGlobal.MetadataProvider -> NavEnvironment
            // NCLOptionMetadata.Default creates a simple default metadata without NavGlobal access
            if (exprText == "NCLEnumMetadata" && methodName == "Create")
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("NCLOptionMetadata"),
                    SyntaxFactory.IdentifierName("Default"));
            }

            // ALCompiler.ToNavValue(x) -> AlCompat.ToNavValue(x)
            // ToNavValue chains through NavValueFormatter -> NavSession -> NavEnvironment
            if (exprText == "ALCompiler" && methodName == "ToNavValue")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ToNavValue")));
            }

            // ALCompiler.ObjectToExactNavValue<T>(x) -> (T)(object)x
            if (exprText == "ALCompiler" && methodName == "ObjectToExactNavValue")
            {
                var arg = visited.ArgumentList.Arguments[0].Expression;
                // Extract T from the generic method name
                if (memberAccess.Name is GenericNameSyntax genericName &&
                    genericName.TypeArgumentList.Arguments.Count == 1)
                {
                    var targetType = genericName.TypeArgumentList.Arguments[0];
                    return SyntaxFactory.CastExpression(
                        targetType,
                        SyntaxFactory.ParenthesizedExpression(
                            SyntaxFactory.CastExpression(
                                SyntaxFactory.PredefinedType(SyntaxFactory.Token(SyntaxKind.ObjectKeyword)),
                                SyntaxFactory.ParenthesizedExpression(arg))));
                }
            }

            // ALCompiler.ObjectToDecimal -> AlCompat.ObjectToDecimal
            // ObjectToDecimal accesses NavSession for culture-aware parsing; our version is simpler.
            if (exprText == "ALCompiler" && methodName == "ObjectToDecimal")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ObjectToDecimal")));
            }

            // ALCompiler.ToInterface(this, codeunit) -> codeunit (strip interface wrapper)
            if (exprText == "ALCompiler" && methodName == "ToInterface")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 2)
                    return args[1].Expression;
            }

            // ALCompiler.ObjectToExactINavRecordHandle(x) -> (MockRecordHandle)x
            if (exprText == "ALCompiler" && methodName == "ObjectToExactINavRecordHandle")
            {
                var arg = visited.ArgumentList.Arguments[0].Expression;
                return SyntaxFactory.CastExpression(
                    SyntaxFactory.ParseTypeName("MockRecordHandle"),
                    SyntaxFactory.ParenthesizedExpression(arg));
            }

            // ALCompiler.NavIndirectValueToDecimal(x) -> AlCompat.ObjectToDecimal(x)
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToDecimal")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ObjectToDecimal")));
            }

            // ALCompiler.NavIndirectValueToBoolean(x) -> AlCompat.NavIndirectValueToBoolean(x)
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToBoolean")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("NavIndirectValueToBoolean")));
            }

            // ALCompiler.NavIndirectValueToInt32(x) -> AlCompat.NavIndirectValueToInt32(x)
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToInt32")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("NavIndirectValueToInt32")));
            }

            // ALCompiler.NavIndirectValueToNavValue<T>(x) -> AlCompat.NavIndirectValueToNavValue<T>(x)
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToNavValue")
            {
                // Preserve the generic type arguments (e.g., <NavText>)
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        memberAccess.Name)); // keep GenericNameSyntax with type args
            }

            // ALCompiler.NavIndirectValueToINavRecordHandle(x) -> (MockRecordHandle)x
            if (exprText == "ALCompiler" && methodName == "NavIndirectValueToINavRecordHandle")
            {
                var arg = visited.ArgumentList.Arguments[0].Expression;
                return SyntaxFactory.CastExpression(
                    SyntaxFactory.ParseTypeName("MockRecordHandle"),
                    SyntaxFactory.ParenthesizedExpression(arg));
            }

            // ALCompiler.ToVariant(this, value) -> AlCompat.ToVariant(value)
            // ALCompiler.NavValueToVariant(this, value) -> AlCompat.ToVariant(value)
            if (exprText == "ALCompiler" && (methodName == "ToVariant" || methodName == "NavValueToVariant"))
            {
                var args = visited.ArgumentList.Arguments;
                // Skip the first 'this' argument (ITreeObject)
                if (args.Count >= 2)
                {
                    var valueArg = args[1];
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("ToVariant")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(valueArg)));
                }
            }

            // NavInStream.Default(this) -> NavInStream.Default(null!)
            // NavOutStream.Default(this) -> NavOutStream.Default(null!)
            // NavFieldRef.Default(this) -> NavFieldRef.Default(null!)
            // These BC types require ITreeObject but work with null in standalone mode.
            if ((exprText == "NavInStream" || exprText == "NavOutStream" || exprText == "NavFieldRef")
                && methodName == "Default")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count == 1 && args[0].Expression.ToString() == "this")
                {
                    var nullBang = SyntaxFactory.PostfixUnaryExpression(
                        SyntaxKind.SuppressNullableWarningExpression,
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                    return visited.WithArgumentList(SyntaxFactory.ArgumentList(
                        SyntaxFactory.SingletonSeparatedList(SyntaxFactory.Argument(nullBang))));
                }
            }

            // ALSystemString.ALLowercase(x) -> (x?.ToLowerInvariant() ?? "")
            // ALSystemString.ALUppercase(x) -> (x?.ToUpperInvariant() ?? "")
            // These BC runtime methods access NavEnvironment for CultureInfo, crashing on Linux.
            if (exprText == "ALSystemString" && (methodName == "ALLowercase" || methodName == "ALUppercase"))
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count == 1)
                {
                    var netMethod = methodName == "ALLowercase" ? "ToLowerInvariant" : "ToUpperInvariant";
                    // Build: (x?.ToLowerInvariant() ?? "")
                    var arg = args[0].Expression;
                    var nullCoalesce = SyntaxFactory.ParenthesizedExpression(
                        SyntaxFactory.BinaryExpression(
                            SyntaxKind.CoalesceExpression,
                            SyntaxFactory.ConditionalAccessExpression(
                                arg,
                                SyntaxFactory.InvocationExpression(
                                    SyntaxFactory.MemberBindingExpression(
                                        SyntaxFactory.IdentifierName(netMethod)))),
                            SyntaxFactory.LiteralExpression(
                                SyntaxKind.StringLiteralExpression,
                                SyntaxFactory.Literal(""))));
                    return nullCoalesce;
                }
            }

            // ALSystemDate.ALWorkDate(null!) -> ALSystemDate.ALWorkDate(NavDate.Default)
            // The rewriter turns this.Session -> null!, which makes ALWorkDate ambiguous between
            // the NavSession and NavDate overloads. We disambiguate to the NavDate overload.
            if (exprText == "ALSystemDate" && methodName == "ALWorkDate")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count == 1)
                {
                    var argText = args[0].Expression.ToString();
                    // Match null!, default! or similar null patterns from Session rewriting
                    if (argText.Contains("null"))
                    {
                        return SyntaxFactory.InvocationExpression(
                            visited.Expression,
                            SyntaxFactory.ArgumentList(
                                SyntaxFactory.SingletonSeparatedList(
                                    SyntaxFactory.Argument(
                                        SyntaxFactory.DefaultExpression(
                                            SyntaxFactory.ParseTypeName("NavDate"))))));
                    }
                }
            }

            // ALCompiler.NavRecordToVariant(this, record) -> AlCompat.ToVariant(record)
            // Strip the ITreeObject first argument
            if (exprText == "ALCompiler" && methodName == "NavRecordToVariant")
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 2)
                {
                    var recordArg = args[1];
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("ToVariant")),
                        SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(recordArg)));
                }
            }

            // ALCompiler.ObjectToInt32(x) -> Convert.ToInt32(x)
            // Simple numeric conversion
            if (exprText == "ALCompiler" && methodName == "ObjectToInt32")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("Convert"),
                        SyntaxFactory.IdentifierName("ToInt32")));
            }

            // ALCompiler.ObjectToBoolean(x) -> AlCompat.ObjectToBoolean(x)
            if (exprText == "ALCompiler" && methodName == "ObjectToBoolean")
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName("ObjectToBoolean")));
            }

            // ALSystemNumeric.ALRandomize(seed) -> AlCompat.ALRandomize(seed)
            // ALSystemNumeric.ALRandom(max) -> AlCompat.ALRandom(max)
            // ALRandomize/ALRandom require NavSession; our versions use System.Random.
            if (exprText == "ALSystemNumeric" && (methodName == "ALRandomize" || methodName == "ALRandom"))
            {
                return visited.WithExpression(
                    SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlCompat"),
                        SyntaxFactory.IdentifierName(methodName)));
            }

            // value.ALByValue(this) -> value  (strip ITreeObject calls)
            // value.ModifyLength(N) -> value  (strip length modification)
            if (StripITreeObjectArgMethods.Contains(methodName))
            {
                // Return just the expression the method is called on
                return memberAccess.Expression;
            }

            // blob.ALCreateInStream(this, inStr) -> blob.ALCreateInStream(null!, inStr)
            // Replace the first 'this' (ITreeObject) argument with null! but keep the rest.
            // These are real BC runtime methods that require ITreeObject; they work with null
            // in standalone mode for the operations we support.
            if (NullifyFirstThisArgMethods.Contains(methodName))
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 1 && args[0].Expression.ToString() == "this")
                {
                    var nullBang = SyntaxFactory.PostfixUnaryExpression(
                        SyntaxKind.SuppressNullableWarningExpression,
                        SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression));
                    var newArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    newArgs = newArgs.Add(SyntaxFactory.Argument(nullBang));
                    for (int i = 1; i < args.Count; i++)
                        newArgs = newArgs.Add(args[i]);
                    return visited.WithArgumentList(SyntaxFactory.ArgumentList(newArgs));
                }
            }

            // recRef.ALField(this, fieldNo) -> recRef.ALField(fieldNo)
            // Strip the first 'this' (ITreeObject) argument from mock type methods.
            if (StripFirstThisArgMethods.Contains(methodName))
            {
                var args = visited.ArgumentList.Arguments;
                if (args.Count >= 2 && args[0].Expression.ToString() == "this")
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 1; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);
                    return visited.WithArgumentList(SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // NavFormatEvaluateHelper.Format(this.Session, value) -> AlCompat.Format(value)
            if (exprText == "NavFormatEvaluateHelper" && methodName == "Format")
            {
                var args = visited.ArgumentList.Arguments;
                // Skip the first 'this.Session' argument
                if (args.Count >= 2)
                {
                    var keptArgs = new SeparatedSyntaxList<ArgumentSyntax>();
                    for (int i = 1; i < args.Count; i++)
                        keptArgs = keptArgs.Add(args[i]);
                    return SyntaxFactory.InvocationExpression(
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlCompat"),
                            SyntaxFactory.IdentifierName("Format")),
                        SyntaxFactory.ArgumentList(keptArgs));
                }
            }

            // ALSystemErrorHandling.ALClearLastError() -> AlScope.LastErrorText = ""
            // ALSystemErrorHandling.ALGetLastErrorTextFunc(...) -> AlScope.LastErrorText
            if (exprText == "ALSystemErrorHandling")
            {
                if (methodName == "ALClearLastError")
                {
                    // Return an assignment expression: AlScope.LastErrorText = ""
                    return SyntaxFactory.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        SyntaxFactory.MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            SyntaxFactory.IdentifierName("AlScope"),
                            SyntaxFactory.IdentifierName("LastErrorText")),
                        SyntaxFactory.LiteralExpression(
                            SyntaxKind.StringLiteralExpression,
                            SyntaxFactory.Literal("")));
                }

                if (methodName == "ALGetLastErrorTextFunc")
                {
                    // Return just AlScope.LastErrorText (ignore the excludeCustomerData arg)
                    return SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        SyntaxFactory.IdentifierName("AlScope"),
                        SyntaxFactory.IdentifierName("LastErrorText"));
                }
            }
        }

        return visited;
    }

    // -----------------------------------------------------------------------
    // Member access expressions: remove .Target., rewrite base.Parent._parent
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitMemberAccessExpression(MemberAccessExpressionSyntax node)
    {
        var visited = (MemberAccessExpressionSyntax)base.VisitMemberAccessExpression(node)!;

        // Pattern: base.Parent.xxx -> _parent.xxx
        // After recursion, base.Parent is a MemberAccessExpression where:
        //   Expression = BaseExpression ("base"), Name = "Parent"
        // So base.Parent.field shows up as: (base.Parent).field
        if (visited.Expression is MemberAccessExpressionSyntax innerAccess2 &&
            innerAccess2.Name.Identifier.Text == "Parent" &&
            innerAccess2.Expression is BaseExpressionSyntax)
        {
            // Replace base.Parent.xxx with _parent.xxx
            return visited.WithExpression(SyntaxFactory.IdentifierName("_parent"));
        }

        // Pattern: xxx.Target -> xxx (strip .Target accessor on handles)
        // This covers both xxx.Target.Method (already handled) and standalone xxx.Target
        if (visited.Name.Identifier.Text == "Target")
        {
            // Just strip .Target and return the expression
            return visited.Expression;
        }

        // (NavOptionMetadata access left as-is — NavOption type is preserved)

        // Pattern: xxx.Target.MethodName -> xxx.MethodName (legacy — now redundant but kept for safety)
        if (visited.Expression is MemberAccessExpressionSyntax innerAccess &&
            innerAccess.Name.Identifier.Text == "Target")
        {
            var outerMethodName = visited.Name.Identifier.Text;

            // Record target methods
            if (RecordTargetMethods.Contains(outerMethodName))
            {
                return visited.WithExpression(innerAccess.Expression);
            }

            // Codeunit target method: Invoke
            if (outerMethodName == "Invoke")
            {
                return visited.WithExpression(innerAccess.Expression);
            }

            // Also handle ToDecimal, and other methods that may chain after Target
            // e.g. this.spikeItem.Target.GetFieldValueSafe(3, NavType.Decimal).ToDecimal()
            // The GetFieldValueSafe is caught above; ToDecimal chains on its result, not on Target.
        }

        // Pattern: ALSystemErrorHandling.ALGetLastErrorText -> AlScope.LastErrorText
        // ALSystemErrorHandling accesses NavCurrentThread.Session which is null in standalone mode.
        if (visited.Expression is IdentifierNameSyntax errHandlingId &&
            errHandlingId.Identifier.Text == "ALSystemErrorHandling")
        {
            var errProp = visited.Name.Identifier.Text;
            if (errProp == "ALGetLastErrorText" || errProp == "ALGetLastErrorCode" || errProp == "ALGetLastErrorCallStack")
            {
                return SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlScope"),
                    SyntaxFactory.IdentifierName("LastErrorText"));
            }
        }

        // Pattern: value.ALIsBoolean, value.ALIsText, etc. (NavVariant type-check properties)
        // Rewrite to: AlCompat.ALIsBoolean(value) invocation
        var memberName = visited.Name.Identifier.Text;

        // Pattern: xxx.Session.IsEventSessionRecorderEnabled -> false
        // Also: xxx.Session -> null! (Session property removed with base class)
        if (memberName == "Session" &&
            (visited.Expression is ThisExpressionSyntax || visited.Expression is IdentifierNameSyntax))
        {
            return SyntaxFactory.PostfixUnaryExpression(
                SyntaxKind.SuppressNullableWarningExpression,
                SyntaxFactory.LiteralExpression(SyntaxKind.NullLiteralExpression))
                .WithTriviaFrom(visited);
        }
        if (memberName == "IsEventSessionRecorderEnabled")
        {
            return SyntaxFactory.LiteralExpression(SyntaxKind.FalseLiteralExpression)
                .WithTriviaFrom(visited);
        }
        if (memberName.StartsWith("ALIs") && NavVariantTypeCheckProps.Contains(memberName))
        {
            return SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    SyntaxFactory.IdentifierName("AlCompat"),
                    SyntaxFactory.IdentifierName(memberName)),
                SyntaxFactory.ArgumentList(
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.Argument(visited.Expression))));
        }

        return visited;
    }

    private static readonly HashSet<string> NavVariantTypeCheckProps = new(StringComparer.Ordinal)
    {
        "ALIsBoolean", "ALIsOption", "ALIsInteger", "ALIsByte", "ALIsBigInteger",
        "ALIsDecimal", "ALIsText", "ALIsCode", "ALIsChar", "ALIsTextConst",
        "ALIsDate", "ALIsTime", "ALIsDuration", "ALIsDateTime", "ALIsDateFormula",
        "ALIsGuid", "ALIsRecordId", "ALIsRecord", "ALIsRecordRef", "ALIsFieldRef",
        "ALIsCodeunit", "ALIsFile",
    };

    // -----------------------------------------------------------------------
    // Expression statements: remove StmtHit(N); and handle NavRuntimeHelpers
    // -----------------------------------------------------------------------
    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        // Remove StmtHit(N); statements entirely
        if (node.Expression is InvocationExpressionSyntax invocation)
        {
            if (invocation.Expression is IdentifierNameSyntax ident &&
                ident.Identifier.Text == "StmtHit")
            {
                return null; // remove the statement
            }

            // Remove calls to BC-only methods (ALGetTable, ALClose, RunEvent) that can't work standalone
            if (invocation.Expression is MemberAccessExpressionSyntax stripMa &&
                StripEntireCallMethods.Contains(stripMa.Name.Identifier.Text))
            {
                // Return empty statement instead of null to avoid crash inside using blocks
                return SyntaxFactory.EmptyStatement();
            }

            // NavForm.Run(pageId, record) -> no-op (page navigation not supported standalone)
            if (invocation.Expression is MemberAccessExpressionSyntax navFormMa &&
                navFormMa.Expression.ToString() == "NavForm" &&
                navFormMa.Name.Identifier.Text == "Run")
            {
                return SyntaxFactory.EmptyStatement();
            }

            // NavRuntimeHelpers.CompilationError(...) -> throw new InvalidOperationException("Compilation error");
            if (invocation.Expression is MemberAccessExpressionSyntax ma &&
                ma.Expression.ToString() == "NavRuntimeHelpers" &&
                ma.Name.Identifier.Text == "CompilationError")
            {
                return SyntaxFactory.ThrowStatement(
                    SyntaxFactory.ObjectCreationExpression(
                        SyntaxFactory.ParseTypeName("InvalidOperationException"))
                        .WithArgumentList(SyntaxFactory.ArgumentList(
                            SyntaxFactory.SingletonSeparatedList(
                                SyntaxFactory.Argument(
                                    SyntaxFactory.LiteralExpression(
                                        SyntaxKind.StringLiteralExpression,
                                        SyntaxFactory.Literal("Compilation error")))))));
            }
        }

        var visited = base.VisitExpressionStatement(node);
        // After visiting children, CStmtHit(N) becomes `true;` which is not a valid statement
        if (visited is ExpressionStatementSyntax exprStmt &&
            exprStmt.Expression is LiteralExpressionSyntax literal &&
            literal.Kind() == SyntaxKind.TrueLiteralExpression)
        {
            return null; // remove the dead statement
        }
        return visited;
    }
}
