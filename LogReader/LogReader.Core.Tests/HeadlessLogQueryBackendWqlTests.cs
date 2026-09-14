namespace LogReader.Core.Tests;

using System.Text.Json;
using LogReader.Core;
using LogReader.Core.Models;
using LogReader.Infrastructure.Services;

public sealed partial class HeadlessLogQueryBackendTests
{
    private static ConfiguredLogCatalogSnapshot WithProfiles(ConfiguredLogCatalogSnapshot snapshot, params StructuredFieldProfile[] profiles)
        => new(snapshot.SourceFormatVersion, snapshot.Groups, snapshot.Files, snapshot.DatePathPatterns, fieldProfiles: profiles);

    private static LogWqlQuery Wql(string expression, string? profileId = "example", string? cursor = null, int? maxFiles = null)
        => new() { Targets = [new(ConfiguredLogTargetKind.Dashboard, "dashboard")], Query = expression, ProfileId = profileId, Cursor = cursor, MaxFiles = maxFiles };

    [Fact]
    public async Task Wql_DiscoveryAndQueryMatchSharedEngineWithContextAndNoPatternsOrPaths()
    {
        const string line = "ERROR duration=842 customer=00123";
        var path = await CreateFileAsync("wql.log", "before\n" + line + "\nINFO duration=bad\nafter");
        var snapshot = WithProfiles(CreateSnapshot(("file", path)), WqlTests.Profile());
        using var backend = CreateBackend(snapshot);
        var profiles = await backend.ListFieldProfilesAsync();
        Assert.Equal("example", Assert.Single(profiles.Result!.Profiles).Id);
        Assert.Equal(2, profiles.Result.Builtins.Length);
        Assert.DoesNotContain("Pattern", JsonSerializer.Serialize(profiles));
        var query = new LogWqlQuery
        {
            Targets = [new(ConfiguredLogTargetKind.Dashboard, "dashboard")], Query = "level = \"ERROR\" AND duration_ms > 500",
            ProfileId = "example", IncludeContextBefore = 1, IncludeContextAfter = 1
        };
        var result = await backend.QueryLogsAsync(query);
        Assert.Empty(result.Errors);
        var file = Assert.Single(result.Result!.Files);
        var hit = Assert.Single(file.Hits);
        Assert.Equal(2, hit.LineNumber);
        Assert.Equal("before", Assert.Single(hit.ContextBefore).Text);
        Assert.Equal("INFO duration=bad", Assert.Single(hit.ContextAfter).Text);
        var expected = WqlCompiler.Compile(query.Query, WqlTests.Profile()).Evaluate(line, 2);
        Assert.Equal(expected.Fields.OrderBy(field => field.Key), hit.Fields.OrderBy(field => field.Key));
        Assert.True(file.Parsing!.IsScanComplete);
        Assert.Equal(4, file.Parsing.EvaluatedLineCount);
        Assert.Equal(1, file.Parsing.Fields["duration_ms"].InvalidCount);
        Assert.True(result.Result.IsQueryComplete);
        Assert.DoesNotContain(_testDirectory, JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);

        var legacy = await backend.SearchLogsAsync(Search("dashboard", "ERROR"));
        var serialized = JsonSerializer.Serialize(legacy);
        Assert.DoesNotContain("Wql", serialized);
        Assert.DoesNotContain("FieldStatistics", serialized);
    }

    [Fact]
    public async Task Wql_InvalidQueriesAndProfilesAreRejectedBeforeLogProbes()
    {
        var probes = 0;
        using var backend = CreateBackend(WithProfiles(CreateSnapshot(("file", "never-opened.log")), WqlTests.Profile()), pathExists: _ => { probes++; return true; });
        var invalid = await backend.QueryLogsAsync(Wql("unknown = 1"));
        Assert.Equal("invalid_wql", Assert.Single(invalid.Errors).Code);
        Assert.Contains("position", invalid.Errors[0].Message);
        var missing = await backend.QueryLogsAsync(Wql("level = \"ERROR\"", "deleted"));
        Assert.Equal("unknown_field_profile", Assert.Single(missing.Errors).Code);
        Assert.Equal(0, probes);
    }

    [Fact]
    public async Task Wql_FilePagingBindsProfileRevisionAndRejectsCrossToolCursors()
    {
        var first = await CreateFileAsync("wql-a.log", "ERROR");
        var second = await CreateFileAsync("wql-b.log", "ERROR");
        var snapshot = WithProfiles(CreateSnapshot(("a", first), ("b", second)), WqlTests.Profile());
        using var backend = CreateBackend(snapshot);
        var page = await backend.QueryLogsAsync(Wql("level = \"ERROR\"", maxFiles: 1));
        Assert.NotNull(page.Result!.NextCursor);
        Assert.False(page.Result.IsQueryComplete);
        var next = await backend.QueryLogsAsync(Wql("level = \"ERROR\"", cursor: page.Result.NextCursor, maxFiles: 1));
        Assert.True(next.Result!.IsQueryComplete);
        Assert.Equal("b", Assert.Single(next.Result.Files).FileId);
        var changed = WqlTests.Profile();
        changed.Fields[0].Pattern = "(?<level>INFO)";
        using var changedBackend = CreateBackend(WithProfiles(snapshot, changed));
        var stale = await changedBackend.QueryLogsAsync(Wql("level = \"ERROR\"", cursor: page.Result.NextCursor, maxFiles: 1));
        Assert.Equal("mismatched_search_cursor", Assert.Single(stale.Errors).Code);
        var crossTool = await backend.SearchLogsAsync(new LogSearchQuery
        {
            Targets = [new(ConfiguredLogTargetKind.Dashboard, "dashboard")], Query = "level = \"ERROR\"",
            MaxFiles = 1, Cursor = page.Result.NextCursor
        });
        Assert.Equal("mismatched_search_cursor", Assert.Single(crossTool.Errors).Code);
    }

    [Fact]
    public async Task Wql_BudgetsAndSanitizationDoNotChangePredicateOrScanCoverage()
    {
        var path = await CreateFileAsync("wql-budget.log", "ERROR duration=842 payload=\u001b" + new string('x', 9000));
        var profile = WqlTests.Profile();
        profile.Fields.Add(new() { Name = "payload", Pattern = "payload=(?<payload>.*)" });
        using var backend = CreateBackend(WithProfiles(CreateSnapshot(("file", path)), profile),
            limits: LogQueryEffectiveLimits.Default with { MaximumResponseCharacters = 1000 });
        var response = await backend.QueryLogsAsync(Wql("payload CONTAINS \"xxx\""));
        var file = Assert.Single(response.Result!.Files);
        var hit = Assert.Single(file.Hits);
        Assert.True(response.IsTruncated);
        Assert.True(hit.IsTextTruncated || hit.AreFieldsTruncated);
        Assert.True(file.Parsing!.IsScanComplete);
        Assert.Equal(1, file.Parsing.EvaluatedLineCount);
        Assert.DoesNotContain('\u001b', hit.Text);
        Assert.All(hit.Fields.Values, value => Assert.DoesNotContain('\u001b', value.Text ?? ""));
        Assert.True(hit.Text.Length + hit.Fields.Sum(field => field.Key.Length + (field.Value.Text?.Length ?? 0)) < 1000);
    }

    [Fact]
    public async Task Wql_TimeBoundsBuiltinsAndCandidateAuthorizationArePreserved()
    {
        var path = await CreateFileAsync("wql-time.log", "2026-09-13 10:00:00 ERROR\n2026-09-13 11:00:00 ERROR");
        using var backend = CreateBackend(CreateSnapshot(("file", path)));
        var response = await backend.QueryLogsAsync(new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "raw CONTAINS \"error\" AND line_number > 1",
            StartTimestamp = "2026-09-13 11:00:00"
        });
        Assert.Equal(2, Assert.Single(Assert.Single(response.Result!.Files).Hits).LineNumber);
        Assert.Equal(1, response.Result.Files[0].Parsing!.EvaluatedLineCount);
        var rejected = await backend.QueryLogsAsync(new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "not-configured")], Query = "line_number > 0"
        });
        Assert.True(rejected.Result == null || rejected.Result.Files.All(file => file.Hits.IsEmpty));
    }

    [Fact]
    public async Task Wql_HitLimitAndRegexTimeoutAreExplicitlyIncomplete()
    {
        var path = await CreateFileAsync("wql-limit.log", "ERROR\nERROR\nERROR");
        using var backend = CreateBackend(WithProfiles(CreateSnapshot(("file", path)), WqlTests.Profile()));
        var limited = await backend.QueryLogsAsync(new()
        {
            Targets = [new(ConfiguredLogTargetKind.LogFile, "file")], Query = "level = \"ERROR\"", ProfileId = "example", MaxHitsPerFile = 1
        });
        Assert.False(limited.Result!.IsQueryComplete);
        Assert.False(limited.Result.Files[0].Parsing!.IsScanComplete);
        Assert.Single(limited.Result.Files[0].Hits);
        var slowPath = await CreateFileAsync("slow.log", new string('a', 50000) + "!");
        var profile = new StructuredFieldProfile { Id = "slow", Name = "Slow", Fields = [new() { Name = "value", Pattern = "^(?<value>(a+)+)$" }] };
        using var slow = CreateBackend(WithProfiles(CreateSnapshot(("slow", slowPath)), profile));
        var timedOut = await slow.QueryLogsAsync(Wql("value IS MISSING", "slow"));
        Assert.False(timedOut.Result!.IsQueryComplete);
        Assert.Equal("field_regex_timeout", timedOut.Result.Files[0].Error!.Code);
        Assert.False(timedOut.Result.Files[0].Parsing!.IsScanComplete);
    }
}
