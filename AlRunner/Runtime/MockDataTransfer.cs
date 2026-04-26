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

    /// <summary>
    /// DataTransfer.UpdateAuditFields — when true, BC auto-populates SystemModifiedAt/By
    /// on the target records during CopyRows/CopyFields. Default is false.
    /// In standalone mode there is no real database so audit-field propagation is a no-op,
    /// but the property must still round-trip for test code that reads it back.
    /// </summary>
    public bool ALUpdateAuditFields { get; set; }

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

    /// <summary>
    /// AddDestinationFilter(Integer, Text, Joker) — 3-argument destination filter.
    /// BC lowers the 3rd argument (the filter value, which is a "Joker"/any-type in AL)
    /// to <c>object</c> in C#. No-op in standalone mode.
    /// </summary>
    public void ALAddDestinationFilter(int fieldNo, string filterExpression, object? value)
    {
        // No-op: destination filtering is a DataTransfer bulk-data feature not supported standalone.
    }

    public void ALAddDestinationFilter(int fieldNo, NavText filterExpression, object? value)
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

    /// <summary>Reset the DataTransfer to its initial state.</summary>
    public void Clear()
    {
        _sourceTableId = 0;
        _targetTableId = 0;
        _fieldMappings.Clear();
        _constantValues.Clear();
        ALUpdateAuditFields = false;
    }
}
