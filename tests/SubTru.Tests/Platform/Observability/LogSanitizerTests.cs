using System.Text.RegularExpressions;
using SubTru.Api.Platform.Observability;

namespace SubTru.Tests.Platform.Observability;

public sealed class LogSanitizerTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Fingerprint_EmptyOrBlankValue_ReturnsMissing(string? value)
    {
        var fingerprint = LogSanitizer.Fingerprint(value);

        Assert.Equal("missing", fingerprint);
    }

    [Fact]
    public void Fingerprint_ReturnsDeterministicTrimmedLowercaseTwelveCharacterHexValue()
    {
        var first = LogSanitizer.Fingerprint("  sensitive-value  ");
        var second = LogSanitizer.Fingerprint("sensitive-value");

        Assert.Equal(second, first);
        Assert.Equal(12, first.Length);
        Assert.Matches(new Regex("^[0-9a-f]{12}$"), first);
    }
}
