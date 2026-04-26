/// Source codeunit for Dictionary of [Guid, Codeunit] tests (issues #1239, #1240, #1241).
/// Exercises NavObjectDictionary<TKey, TValue> where TValue is a codeunit type:
///   - Add / ContainsKey / Get (out-param and return) / Remove / Keys()
codeunit 1239001 "Dict Cu Manager"
{
    var
        ActiveTasks: Dictionary of [Guid, Codeunit "Dict Cu Task Handle"];

    /// Register a task handle under the given ID.
    procedure Register(TaskID: Guid; TaskHandle: Codeunit "Dict Cu Task Handle")
    begin
        ActiveTasks.Add(TaskID, TaskHandle);
    end;

    /// Return true if the task ID is registered.
    procedure HasTask(TaskID: Guid): Boolean
    begin
        exit(ActiveTasks.ContainsKey(TaskID));
    end;

    /// Get the handle by ID using the out-parameter overload (issue #1240).
    procedure TryGet(TaskID: Guid; var TaskHandle: Codeunit "Dict Cu Task Handle"): Boolean
    begin
        exit(ActiveTasks.Get(TaskID, TaskHandle));
    end;

    /// Get the number of registered tasks.
    procedure Count(): Integer
    begin
        exit(ActiveTasks.Count());
    end;

    /// Return all registered task IDs (issue #1239 / #1241 — Keys() call).
    procedure GetKeys(): List of [Guid]
    begin
        exit(ActiveTasks.Keys());
    end;

    /// Remove a task and return whether it was present.
    procedure Deregister(TaskID: Guid): Boolean
    begin
        exit(ActiveTasks.Remove(TaskID));
    end;
}

codeunit 1239002 "Dict Cu Task Handle"
{
    var
        Name: Text;

    procedure SetName(n: Text)
    begin
        Name := n;
    end;

    procedure GetName(): Text
    begin
        exit(Name);
    end;
}
