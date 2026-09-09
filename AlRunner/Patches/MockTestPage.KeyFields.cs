// MockTestPage.KeyFields.cs — the field numbers behind ITestFilter.GetCurrentKeyFields.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AlRunner.Patches;
using Microsoft.Dynamics.Nav.Runtime;
using Microsoft.Dynamics.Nav.Types;
using Microsoft.Dynamics.Nav.Types.Data;
using Microsoft.Dynamics.Nav.Types.Exceptions;

namespace AlRunner;
/// <summary>
/// The field numbers of a record's current key, for <c>ITestFilter.GetCurrentKeyFields</c>
/// (#3316). Shared by the two implementations that back a filter with a real NavRecord —
/// <see cref="LiveNavTestPage"/> and RequestPageTestPage's data-item filter — so the two
/// cannot answer the same question differently.
/// </summary>
internal static class TestFilterKeyFields
{
    /// <summary>
    /// NCLMetaKey's field list is internal and NavRecord exposes only ALCurrentKey (the key
    /// rendered by name) and ALCurrentKeyIndex, so the numbers are recovered by resolving each
    /// rendered field name against the table's own metadata. A name the table does not know is
    /// skipped rather than guessed at: an invented field number would be a wrong answer, where
    /// a short list is a visibly incomplete one.
    /// </summary>
    internal static int[] Of(NavRecord record)
    {
        var names = SplitRenderedKey(record.ALCurrentKey);
        if (names.Length == 0) return Array.Empty<int>();

        var nos = new List<int>();
        foreach (var name in names)
            foreach (var field in record.MetaTable.Fields)
            {
                if (!string.Equals(field.FieldName, name, StringComparison.OrdinalIgnoreCase)) continue;
                nos.Add(field.FieldNo);
                break;
            }
        return nos.ToArray();
    }

    /// <summary>
    /// The field names out of a key as NavRecord.ALCurrentKey renders it — comma-separated,
    /// each name optionally quoted because BC quotes any name that is not a bare identifier
    /// ("Entry No." carries a space and a period). Split out from <see cref="Of"/> so the
    /// parsing can be asserted without a live NavRecord.
    /// </summary>
    internal static string[] SplitRenderedKey(string? rendered)
    {
        if (string.IsNullOrEmpty(rendered)) return Array.Empty<string>();

        var names = new List<string>();
        foreach (var part in rendered!.Split(','))
        {
            var name = part.Trim().Trim('"');
            if (name.Length != 0) names.Add(name);
        }
        return names.ToArray();
    }
}
