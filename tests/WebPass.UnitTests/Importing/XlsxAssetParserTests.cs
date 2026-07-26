using ClosedXML.Excel;
using WebPass.Web.Infrastructure.Importing;
using Xunit;

namespace WebPass.UnitTests.Importing;

public sealed class XlsxAssetParserTests
{
    [Fact]
    public async Task Xlsx_row_is_read_as_literal_cell_values()
    {
        await using var source = Workbook(row =>
        {
            row.Cell(1).Value = "10.0.0.10";
            row.Cell(2).Value = "DC";
            row.Cell(3).Value = "Alive";
            row.Cell(4).Value = "server-10";
            row.Cell(5).Value = "ERP";
            row.Cell(9).Value = "server-password";
        });
        var parser = new XlsxAssetParser();

        var rows = new List<ImportSourceRow>();
        await foreach (var row in parser.ParseAsync(source, default))
        {
            rows.Add(row);
        }

        var parsed = Assert.Single(rows);
        Assert.Equal("10.0.0.10", parsed.BusinessIp);
        Assert.Equal("server-password", parsed.Password);
    }

    [Fact]
    public async Task Formula_cell_is_rejected_without_echoing_its_value()
    {
        await using var source = Workbook(row =>
        {
            row.Cell(1).FormulaA1 = "CONCATENATE(\"10.0.0.\",\"10\")";
        });
        var parser = new XlsxAssetParser();

        var exception = await Assert.ThrowsAsync<FormatException>(async () =>
        {
            await foreach (var _ in parser.ParseAsync(source, default))
            {
            }
        });

        Assert.DoesNotContain("CONCATENATE", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static MemoryStream Workbook(Action<IXLRow> configure)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Servers");
        var headers = new[]
        {
            "BusinessIp", "Location", "AliveStatus", "ComputerName", "SystemName",
            "OperatingSystemVersion", "DatabaseVersion", "Notes", "Password",
        };
        for (var column = 1; column <= headers.Length; column++)
        {
            sheet.Cell(1, column).Value = headers[column - 1];
        }
        configure(sheet.Row(2));
        var stream = new MemoryStream();
        workbook.SaveAs(stream);
        stream.Position = 0;
        return stream;
    }
}
