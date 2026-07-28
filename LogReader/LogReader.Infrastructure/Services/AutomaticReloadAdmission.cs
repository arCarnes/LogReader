namespace LogReader.Infrastructure.Services;

using System.Diagnostics;
using LogReader.Core.Models;

internal sealed class AutomaticReloadAdmission
{
    internal const long MinimumChargeBytes = 16L * 1024 * 1024;
    internal const long PerFileBytesPerSecond = 1024L * 1024;
    internal const long ApplicationBytesPerSecond = 2L * 1024 * 1024;
    internal static readonly TimeSpan MinimumCooldown = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly Func<long> _getTimestamp;
    private long _applicationNotBeforeTimestamp;

    public AutomaticReloadAdmission(Func<long>? getTimestamp = null)
    {
        _getTimestamp = getTimestamp ?? Stopwatch.GetTimestamp;
    }

    public bool TryAdmit(LineIndex index, long snapshotLength, out TimeSpan retryAfter)
    {
        ArgumentNullException.ThrowIfNull(index);

        var now = _getTimestamp();
        lock (_gate)
        {
            var notBefore = Math.Max(
                index.AutomaticReloadNotBeforeTimestamp,
                _applicationNotBeforeTimestamp);
            if (notBefore > now)
            {
                retryAfter = TimestampDeltaToTimeSpan(notBefore - now);
                return false;
            }

            var charge = Math.Max(MinimumChargeBytes, Math.Max(0, snapshotLength));
            index.AutomaticReloadNotBeforeTimestamp = AddDuration(
                now,
                CalculateCooldown(charge, PerFileBytesPerSecond));
            _applicationNotBeforeTimestamp = AddDuration(
                now,
                CalculateCooldown(charge, ApplicationBytesPerSecond));
        }

        retryAfter = TimeSpan.Zero;
        return true;
    }

    public TimeSpan GetRetryAfter(LineIndex index)
    {
        var now = _getTimestamp();
        lock (_gate)
        {
            var notBefore = Math.Max(
                index.AutomaticReloadNotBeforeTimestamp,
                _applicationNotBeforeTimestamp);
            return notBefore > now
                ? TimestampDeltaToTimeSpan(notBefore - now)
                : TimeSpan.Zero;
        }
    }

    internal static TimeSpan CalculateCooldown(long chargeBytes, long bytesPerSecond)
    {
        var seconds = (double)chargeBytes / bytesPerSecond;
        var proportional = seconds >= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.MaxValue
            : TimeSpan.FromSeconds(seconds);
        return proportional > MinimumCooldown
            ? proportional
            : MinimumCooldown;
    }

    private static long AddDuration(long timestamp, TimeSpan duration)
    {
        var timestampDelta = duration.TotalSeconds * Stopwatch.Frequency;
        if (timestampDelta >= long.MaxValue - timestamp)
            return long.MaxValue;

        return timestamp + (long)Math.Ceiling(timestampDelta);
    }

    private static TimeSpan TimestampDeltaToTimeSpan(long timestampDelta)
    {
        var seconds = (double)timestampDelta / Stopwatch.Frequency;
        return seconds >= TimeSpan.MaxValue.TotalSeconds
            ? TimeSpan.MaxValue
            : TimeSpan.FromSeconds(seconds);
    }
}
