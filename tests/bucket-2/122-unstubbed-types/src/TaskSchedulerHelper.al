codeunit 59822 "TaskScheduler Helper"
{
    procedure CreateTaskReturnsGuid(): Guid
    begin
        exit(TaskScheduler.CreateTask(Codeunit::"TaskScheduler Helper", 0, true));
    end;

    procedure TaskExistsReturnsBool(): Boolean
    var
        TaskId: Guid;
    begin
        TaskId := TaskScheduler.CreateTask(Codeunit::"TaskScheduler Helper", 0, true);
        exit(TaskScheduler.TaskExists(TaskId));
    end;

    procedure CancelTaskReturnsTrue(): Boolean
    var
        TaskId: Guid;
    begin
        TaskId := TaskScheduler.CreateTask(Codeunit::"TaskScheduler Helper", 0, true);
        exit(TaskScheduler.CancelTask(TaskId));
    end;

    procedure SetTaskReadyReturnsTrue(): Boolean
    var
        TaskId: Guid;
    begin
        TaskId := TaskScheduler.CreateTask(Codeunit::"TaskScheduler Helper", 0, true);
        exit(TaskScheduler.SetTaskReady(TaskId));
    end;
}
