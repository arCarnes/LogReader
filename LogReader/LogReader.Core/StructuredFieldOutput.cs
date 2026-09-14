namespace LogReader.Core;

using System.Collections.Immutable;
using LogReader.Core.Models;

public static class StructuredFieldOutput
{
    /// <summary>Share one text budget across all extracted values; never truncate before evaluation.</summary>
    public static ImmutableDictionary<string, StructuredFieldValue> Retain(
        IReadOnlyDictionary<string, StructuredFieldValue> fields, int textBudget,
        Func<string, string>? normalize = null)
    {
        var output = ImmutableDictionary.CreateBuilder<string, StructuredFieldValue>(StringComparer.OrdinalIgnoreCase);
        var remaining = Math.Max(0, textBudget);
        var textFields = fields.Count(field => field.Value.Text != null);
        foreach (var field in fields.OrderBy(field => field.Key, StringComparer.OrdinalIgnoreCase))
        {
            var value = field.Value;
            if (value.Text != null)
            {
                var text = normalize == null ? value.Text : normalize(value.Text);
                var length = Math.Min(text.Length, remaining / textFields--);
                // Avoid retaining half a UTF-16 surrogate pair at a truncation boundary.
                if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
                remaining -= length;
                value = value with { Text = text[..length], IsTruncated = value.IsTruncated || length < text.Length };
            }
            output.Add(field.Key, value);
        }
        return output.ToImmutable();
    }
}
