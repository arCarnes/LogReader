namespace LogReader.Core.Tests;

using System.Globalization;
using LogReader.Core;
using LogReader.Core.Models;

public class WqlTests
{
    internal static StructuredFieldProfile Profile() => new()
    {
        Id = "example", Name = "Example",
        Fields =
        [
            new() { Name = "level", Pattern = @"\b(?<level>ERROR|INFO)\b" },
            new() { Name = "customer_id", Pattern = @"customer=(?<customer_id>\d+)" },
            new() { Name = "duration_ms", Type = StructuredFieldType.Number, Pattern = @"duration=(?<duration_ms>\S+)" }
        ]
    };

    [Theory]
    [InlineData("level = \"ERROR\" AND duration_ms > 500", "ERROR duration=842", true)]
    [InlineData("level = \"ERROR\" AND duration_ms > 500", "ERROR duration=12", false)]
    [InlineData("level = \"error\"", "ERROR", true)]
    [InlineData("LeVeL IN (\"error\", \"warning\")", "ERROR", true)]
    [InlineData("duration_ms IN (10, -2.5, +7)", "duration=-2.5", true)]
    [InlineData("NOT level = \"ERROR\"", "no fields", false)]
    [InlineData("duration_ms != 5", "duration=invalid", false)]
    [InlineData("duration_ms IS MISSING", "duration=invalid", true)]
    [InlineData("duration_ms IS NOT MISSING", "duration=12", true)]
    [InlineData("level = \"ERROR\" OR duration_ms > 10", "ERROR", true)]
    [InlineData("level = \"INFO\" OR level = \"ERROR\" AND duration_ms > 10", "INFO", true)]
    [InlineData("(level = \"INFO\" OR level = \"ERROR\") AND duration_ms > 10", "INFO", false)]
    [InlineData("NOT (level = \"ERROR\" AND duration_ms > 10)", "INFO", true)]
    [InlineData("raw CONTAINS \"hello\" AND line_number >= 2", "HELLO", true)]
    [InlineData("customer_id = \"00123\"", "customer=00123", true)]
    public void Expressions(string expression, string line, bool expected)
        => Assert.Equal(expected, WqlCompiler.Compile(expression, Profile()).Evaluate(line, 2).IsMatch);

    [Theory]
    [InlineData("level > \"ERROR\"")]
    [InlineData("duration_ms = \"10\"")]
    [InlineData("duration_ms CONTAINS \"1\"")]
    [InlineData("unknown = 1")]
    [InlineData("level = ERROR")]
    [InlineData("level = \"ERROR\" GROUP BY level")]
    [InlineData("level IN ()")]
    [InlineData("line_number = 1.2.3")]
    [InlineData("line_number = 999999999999999999999999999999999")]
    [InlineData("raw = \"unterminated")]
    [InlineData("raw = \"bad\\q\"")]
    [InlineData("line_number ! 1")]
    public void InvalidExpressionsIdentifyPosition(string expression)
    {
        var error = Assert.Throws<WqlException>(() => WqlCompiler.Compile(expression, Profile()));
        Assert.InRange(error.Position, 0, expression.Length);
        Assert.Contains("position", error.Message);
    }

    [Fact]
    public void BuiltinsEscapesAndCaseSensitiveComparisons()
    {
        Assert.True(WqlCompiler.Compile("raw = \"a\\\"b\\\\c\\n\"").Evaluate("a\"b\\c\n", 1).IsMatch);
        Assert.False(WqlCompiler.Compile("raw CONTAINS \"error\"", caseSensitive: true).Evaluate("ERROR", 1).IsMatch);
    }

    [Fact]
    public void ExtractionIsIndependentAndPreservesDiagnostics()
    {
        var fields = StructuredFieldExtractor.Compile(Profile()).Extract("customer=00123 duration=oops");
        Assert.Equal("00123", fields["customer_id"].Text);
        Assert.Equal(StructuredFieldState.Missing, fields["level"].State);
        Assert.Equal(StructuredFieldState.Invalid, fields["duration_ms"].State);
        Assert.Null(fields["duration_ms"].Text);
    }

    [Fact]
    public void FirstMatchFirstCaptureAndEmptyText()
    {
        var profile = new StructuredFieldProfile { Name = "Capture", Fields = [new() { Name = "value", Pattern = @"(?<value>\d)+" }] };
        Assert.Equal("1", StructuredFieldExtractor.Compile(profile).Extract("123 456")["value"].Text);
        profile.Fields[0].Pattern = @"(?<value>a*)";
        var empty = StructuredFieldExtractor.Compile(profile).Extract("b")["value"];
        Assert.Equal(StructuredFieldState.Value, empty.State);
        Assert.Equal(string.Empty, empty.Text);
    }

    [Fact]
    public void CompiledPlanIsAnImmutableSnapshot()
    {
        var profile = Profile();
        var plan = WqlCompiler.Compile("level = \"ERROR\"", profile);
        var revision = plan.Extractor.Revision;
        profile.Fields[0].Pattern = "(?<level>INFO)";
        Assert.True(plan.Evaluate("ERROR", 1).IsMatch);
        Assert.NotEqual(revision, StructuredFieldExtractor.Compile(profile).Revision);
    }

    [Fact]
    public void NumericConversionIsCultureIndependent()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Assert.True(WqlCompiler.Compile("duration_ms = 1.5", Profile()).Evaluate("duration=1.5", 1).IsMatch);
            Assert.Equal(StructuredFieldState.Invalid, StructuredFieldExtractor.Compile(Profile()).Extract("duration=1,5")["duration_ms"].State);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void BoundsAndProfileValidation()
    {
        Assert.Throws<WqlException>(() => WqlCompiler.Compile(new string(' ', 8193)));
        Assert.Throws<WqlException>(() => WqlCompiler.Compile(new string('(', 33) + "raw = \"x\"" + new string(')', 33)));
        Assert.Throws<WqlException>(() => WqlCompiler.Compile(string.Concat(Enumerable.Repeat("NOT ", 33)) + "raw = \"x\""));
        var profile = Profile();
        profile.Fields.Add(profile.Fields[0].Copy());
        Assert.Throws<ArgumentException>(() => StructuredFieldExtractor.Compile(profile));
        profile = Profile();
        profile.Fields[0].Name = "raw";
        Assert.Throws<ArgumentException>(() => StructuredFieldExtractor.Compile(profile));
        profile = Profile();
        profile.Fields[0].Pattern = "[";
        Assert.Throws<ArgumentException>(() => StructuredFieldExtractor.Compile(profile));
        profile.Fields[0].Pattern = "ERROR";
        Assert.Throws<ArgumentException>(() => StructuredFieldExtractor.Compile(profile));
        profile.Fields[0].Pattern = new string('x', 8193);
        Assert.Throws<ArgumentException>(() => StructuredFieldExtractor.Compile(profile));
        profile.Fields = Enumerable.Range(0, 33).Select(i => new StructuredFieldDefinition { Name = "f" + i, Pattern = $"(?<f{i}>x)" }).ToList();
        Assert.Throws<ArgumentException>(() => StructuredFieldExtractor.Compile(profile));
    }

    [Fact]
    public void CancellationAndRegexTimeoutDoNotLeakLineText()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => StructuredFieldExtractor.Compile(Profile()).Extract("", cancelled.Token));
        var profile = new StructuredFieldProfile { Name = "Slow", Fields = [new() { Name = "slow", Pattern = @"^(?<slow>(a+)+)$" }] };
        var error = Assert.Throws<TimeoutException>(() => StructuredFieldExtractor.Compile(profile).Extract(new string('a', 50_000) + "!secret"));
        Assert.DoesNotContain("secret", error.ToString());
    }
}
