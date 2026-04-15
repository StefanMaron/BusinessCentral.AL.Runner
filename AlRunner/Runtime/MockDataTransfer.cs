namespace AlRunner.Runtime;

using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;

/// <summary>
/// In-memory replacement for NavDataTransfer.
/// NavDataTransfer requires Microsoft.Dynamics.Nav.CodeAnalysis at runtime.
/// This mock stores the configuration but CopyRows/CopyFields are no-ops —
/// real bulk data transfer requires the BC database engine.
/// </summary>
public class MockDataTransfer
{
    private int _sourceTableId;
    private int _targetTableId;
    private readonly List<(int SourceFieldId, int TargetFieldId)> _fieldMappings = new();
    private readonly List<(object Value, int TargetFieldId)> _constantValues = new();

    /// <summary>Set source and target table IDs.</summary>
    public void ALSetTables(int sourceTableId, int targetTableId)
    {
        _sourceTableId = sourceTableId;
        _targetTableId = targetTableId;
    }

    /// <summary>Map a source field to a target field.</summary>
    public void ALAddFieldValue(int sourceFieldId, int targetFieldId)
    {
        _fieldMappings.Add((sourceFieldId, targetFieldId));
    }

    /// <summary>Set a constant value for a target field.</summary>
    public void ALAddConstantValue(object value, int targetFieldId)
    {
        _constantValues.Add((value, targetFieldId));
    }

    /// <summary>Add a join condition. No-op in standalone mode.</summary>
    public void ALAddJoin(int sourceFieldId, int targetFieldId)
    {
        // No-op: join conditions stored but not enforced
    }

    /// <summary>Add a source filter. No-op in standalone mode.</summary>
    public void ALAddSourceFilter(int sourceFieldId, string filterExpression)
    {
        // No-op
    }

    /// <summary>Copy field values from source to target. No-op in standalone mode.</summary>
    public void ALCopyFields()
    {
        // No-op: requires real database
    }

    /// <summary>Copy rows from source to target. No-op in standalone mode.</summary>
    public void ALCopyRows()
    {
        // No-op: requires real database
    }
}
