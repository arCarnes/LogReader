namespace LogReader.Infrastructure.Services;

using LogReader.Core.Models;

public sealed partial class HeadlessLogQueryBackend
{
    private ConfiguredLogRequestError ContinuationError(ContinuationException error, string kind)
    {
        var message = error.Reason switch
        {
            "session_limit_exceeded" => $"The continuation session limit ({_limits.MaximumContinuationSessions}) is exhausted. Resume existing queries, release observations, or wait for idle sessions to expire.",
            "memory_budget_exceeded" => "The continuation memory budget is exhausted. Retry after active work finishes or retained sessions expire.",
            "query_too_large" => "This investigation exceeds the retained continuation state limit. Report its terminal uncertainty; use a narrower scope only for a requested new investigation.",
            _ => error.Code switch
            {
                "mismatched_query_cursor" => "Restore the original selection, predicates and limits with this cursor; it cannot advance a different investigation.",
                "stale_query_cursor" => "The cursor revision or catalog is stale. Use the latest returned cursor; if authorization changed, rediscover the scope. Report any continuity gap instead of restarting automatically.",
                "handoff_boundary_unavailable" => "Handoff requires exhausted selection and verified, fully evaluated per-file boundaries within the monitor file cap. Continue the original snapshot if it has a cursor; otherwise report terminal uncertainty. No scan or observer was started.",
                _ => "Continuity is unavailable (invalid, expired or prior-process cursor). Report the gap and observed results; a new investigation or observation requires explicit intent."
            }
        };
        return Error(error.Code.Replace("_query_cursor", $"_{kind}_cursor", StringComparison.Ordinal), message,
            retryable: error.Code == "continuation_capacity_exceeded" && error.Reason != "query_too_large") with { Reason = error.Reason };
    }
}
