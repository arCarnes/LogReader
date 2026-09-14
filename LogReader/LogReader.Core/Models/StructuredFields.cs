namespace LogReader.Core.Models;

using System.Collections.Immutable;
using System.Text.Json.Serialization;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StructuredFieldType { Text, Number }

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StructuredFieldState { Value, Missing, Invalid }

public sealed class StructuredFieldDefinition
{
    public string Name { get; set; } = string.Empty;
    public StructuredFieldType Type { get; set; }
    public string Pattern { get; set; } = string.Empty;
    public bool CaseSensitive { get; set; }

    public StructuredFieldDefinition Copy() => new()
    {
        Name = Name, Type = Type, Pattern = Pattern, CaseSensitive = CaseSensitive
    };
}

public sealed class StructuredFieldProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public List<StructuredFieldDefinition> Fields { get; set; } = new();

    public StructuredFieldProfile Copy() => new()
    {
        Id = Id, Name = Name, Fields = Fields.Select(field => field.Copy()).ToList()
    };
}

public sealed record StructuredFieldValue(
    StructuredFieldType Type,
    StructuredFieldState State,
    string? Text = null,
    decimal? Number = null,
    bool IsTruncated = false);

public sealed class StructuredFieldStatistics
{
    public long ValueCount { get; set; }
    public long MissingCount { get; set; }
    public long InvalidCount { get; set; }

    public void Add(StructuredFieldState state)
    {
        if (state == StructuredFieldState.Value) ValueCount++;
        else if (state == StructuredFieldState.Missing) MissingCount++;
        else InvalidCount++;
    }
}

public sealed record WqlLineEvaluation(
    bool IsMatch,
    ImmutableDictionary<string, StructuredFieldValue> Fields);
