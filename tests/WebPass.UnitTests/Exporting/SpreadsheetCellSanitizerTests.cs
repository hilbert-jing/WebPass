using WebPass.Web.Application.Exporting;
using Xunit;

namespace WebPass.UnitTests.Exporting;

public sealed class SpreadsheetCellSanitizerTests
{
    [Theory]
    [InlineData("=2+2", "'=2+2")]
    [InlineData("+SUM(A1:A2)", "'+SUM(A1:A2)")]
    [InlineData("-1+2", "'-1+2")]
    [InlineData("@SUM(A1:A2)", "'@SUM(A1:A2)")]
    [InlineData("server-01", "server-01")]
    [InlineData(null, "")]
    public void Escapes_formula_prefixes(string? source, string expected) =>
        Assert.Equal(expected, SpreadsheetCellSanitizer.Sanitize(source));
}
