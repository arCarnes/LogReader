namespace LogReader.Core.Models;

/// <summary>User settings for process-owned MCP continuation capacity.</summary>
public sealed record McpContinuationLimits
{
    public int MaximumSessions { get; init; } = 16;
    public int MemoryPerQueryMiB { get; init; } = 64;
    public int TotalMemoryMiB { get; init; } = 256;

    public const long BytesPerMiB = 1024L * 1024;

    public string? Validate()
    {
        if (MaximumSessions <= 0 || MemoryPerQueryMiB <= 0 || TotalMemoryMiB <= 0)
            return "Enter positive whole numbers for all three limits.";

        var limits = ToEffectiveLimits();
        var minimum = (GetWorkingReservationBytes(limits) + BytesPerMiB - 1) / BytesPerMiB;
        return TotalMemoryMiB < minimum
            ? $"Total memory must be at least {minimum} MiB to allow one query and its working buffers."
            : null;
    }

    public LogQueryEffectiveLimits ToEffectiveLimits() => LogQueryEffectiveLimits.Default with
    {
        MaximumContinuationSessions = MaximumSessions,
        MaximumContinuationSessionBytes = checked(MemoryPerQueryMiB * BytesPerMiB),
        MaximumContinuationBytes = checked(TotalMemoryMiB * BytesPerMiB)
    };

    public static long GetWorkingReservationBytes(LogQueryEffectiveLimits limits)
        => checked(limits.MaximumContinuationSessionBytes + 6L * limits.MaximumSearchLineBytes +
            512 * 1024L + 12L * limits.MaximumResponseCharacters);
}
