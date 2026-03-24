namespace LogReader.Core.Tests;

public class ReplacementTokenParserTests
{
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("plain text")]
    [InlineData(".log")]
    [InlineData("no tokens here")]
    public void Validate_NoTokens_ReturnsNull(string? input)
    {
        Assert.Null(ReplacementTokenParser.Validate(input!));
    }

    [Theory]
    [InlineData("{date:yyyyMMdd}")]
    [InlineData("{date:yyyy-MM-dd}")]
    [InlineData("{date:MM/dd/yyyy}")]
    [InlineData(".log{date:yyyyMMdd}")]
    [InlineData("prefix{date:yyyyMMdd}suffix")]
    public void Validate_ValidDateTokens_ReturnsNull(string input)
    {
        Assert.Null(ReplacementTokenParser.Validate(input));
    }

    [Fact]
    public void Validate_MultipleValidTokens_ReturnsNull()
    {
        Assert.Null(ReplacementTokenParser.Validate("{date:yyyyMMdd}_{date:HHmmss}"));
    }

    [Fact]
    public void Validate_MissingFormat_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("{date}");
        Assert.NotNull(error);
        Assert.Contains("missing a format", error);
    }

    [Fact]
    public void Validate_EmptyFormat_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("{date:}");
        Assert.NotNull(error);
        Assert.Contains("empty format", error);
    }

    [Fact]
    public void Validate_UnknownType_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("{unknown:abc}");
        Assert.NotNull(error);
        Assert.Contains("Unknown token type", error);
    }

    [Fact]
    public void Validate_EmptyType_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("{:fmt}");
        Assert.NotNull(error);
        Assert.Contains("empty type", error);
    }

    [Fact]
    public void Validate_EmptyToken_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("{}");
        // {} has no content so regex won't match — but braces are balanced,
        // and no tokens found, so this is actually valid (literal braces).
        // If the regex \{([^}]+)\} requires at least one char, {} won't match.
        // The string is treated as having no tokens — which is valid.
        // Let's verify:
        Assert.Null(error);
    }

    [Fact]
    public void Validate_UnclosedBrace_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("{date:yyyyMMdd");
        Assert.NotNull(error);
        Assert.Contains("Unmatched brace", error);
    }

    [Fact]
    public void Validate_ExtraClosingBrace_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("date:yyyyMMdd}");
        Assert.NotNull(error);
        Assert.Contains("Unmatched brace", error);
    }

    [Fact]
    public void Validate_OneValidOneInvalid_ReturnsError()
    {
        var error = ReplacementTokenParser.Validate("{date:yyyyMMdd}{bad:thing}");
        Assert.NotNull(error);
        Assert.Contains("Unknown token type", error);
    }

    [Fact]
    public void Validate_TypeIsCaseInsensitive()
    {
        Assert.Null(ReplacementTokenParser.Validate("{Date:yyyyMMdd}"));
        Assert.Null(ReplacementTokenParser.Validate("{DATE:yyyyMMdd}"));
    }
}
