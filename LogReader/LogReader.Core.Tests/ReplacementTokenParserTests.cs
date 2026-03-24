namespace LogReader.Core.Tests;

public class ReplacementTokenParserTests
{
    [Theory]
    [InlineData("{yyyyMMdd}")]
    [InlineData("{yyyy-MM-dd}")]
    [InlineData("{MM/dd/yyyy}")]
    [InlineData(".log{yyyyMMdd}")]
    [InlineData("prefix{yyyyMMdd}suffix")]
    [InlineData(".log.{yyyy-MM-dd}")]
    public void Validate_ValidDateTokens_ReturnsNull(string input)
    {
        Assert.Null(ReplacementTokenParser.Validate(input));
    }

    [Fact]
    public void Validate_MultipleValidTokens_ReturnsNull()
    {
        Assert.Null(ReplacementTokenParser.Validate("{yyyyMMdd}_{HHmmss}"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("plain text")]
    [InlineData(".log")]
    [InlineData(".txt")]
    [InlineData("no tokens here")]
    public void Validate_NoDatePlaceholders_ReturnsError(string? input)
    {
        var error = ReplacementTokenParser.Validate(input!);
        Assert.NotNull(error);
        Assert.Contains("must contain at least one date placeholder", error);
    }

    [Fact]
    public void Validate_EmptyToken_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("{}");
        Assert.NotNull(error);
        Assert.Contains("empty format", error);
    }

    [Fact]
    public void Validate_LegacySyntax_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("{date:yyyyMMdd}");
        Assert.NotNull(error);
        Assert.Contains("unsupported legacy syntax", error);
    }

    [Fact]
    public void Validate_UnclosedBrace_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("{yyyyMMdd");
        Assert.NotNull(error);
        Assert.Contains("Unmatched brace", error);
    }

    [Fact]
    public void Validate_ExtraClosingBrace_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("yyyyMMdd}");
        Assert.NotNull(error);
        Assert.Contains("Unmatched brace", error);
    }

    [Fact]
    public void Validate_MisorderedBraces_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("}{");
        Assert.NotNull(error);
        Assert.Contains("Unmatched brace", error);
    }

    [Fact]
    public void Validate_NestedTokenBraces_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("{{yyyyMMdd}}");
        Assert.NotNull(error);
        Assert.Contains("Unmatched brace", error);
    }

    [Fact]
    public void Validate_UnterminatedTokenAfterLiteralBraces_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("{}{yyyyMMdd");
        Assert.NotNull(error);
        Assert.Contains("empty format", error);
    }

    [Fact]
    public void Validate_InvalidDateFormat_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("{yyyyMMdd}{not-a-format-%}");
        Assert.NotNull(error);
        Assert.Contains("Invalid date format", error);
    }

    [Fact]
    public void TryExpand_ValidDateTokens_ExpandsUsingReferenceDate()
    {
        var success = ReplacementTokenParser.TryExpand(
            ".log{yyyyMMdd}_{HHmm}",
            new DateTime(2026, 3, 24, 14, 5, 0),
            out var expanded,
            out var error);

        Assert.True(success);
        Assert.Null(error);
        Assert.Equal(".log20260324_1405", expanded);
    }

    [Fact]
    public void TryExpand_InvalidToken_ReturnsError()
    {
        var success = ReplacementTokenParser.TryExpand(
            "{date:value}",
            new DateTime(2026, 3, 24),
            out var expanded,
            out var error);

        Assert.False(success);
        Assert.NotNull(error);
        Assert.Equal("{date:value}", expanded);
    }

    [Fact]
    public void DescribeTokens_ReplacesDateTokensWithFormatStrings()
    {
        var described = ReplacementTokenParser.DescribeTokens(".log{yyyyMMdd}_{HHmm}");

        Assert.Equal(".logyyyyMMdd_HHmm", described);
    }

    [Fact]
    public void DescribeTokens_InvalidPattern_ReturnsOriginalText()
    {
        var described = ReplacementTokenParser.DescribeTokens("{date:yyyyMMdd}");

        Assert.Equal("{date:yyyyMMdd}", described);
    }
}
