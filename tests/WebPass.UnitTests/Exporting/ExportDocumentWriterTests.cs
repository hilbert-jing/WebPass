using System.Text;
using ClosedXML.Excel;
using WebPass.Web.Application.Exporting;
using WebPass.Web.Infrastructure.Exporting;
using Xunit;

namespace WebPass.UnitTests.Exporting;

public sealed class ExportDocumentWriterTests
{
    private readonly ExportDocumentWriter _writer = new();

    [Fact]
    public void Ordinary_xlsx_has_exact_secret_free_headers()
    {
        var file = _writer.WriteOrdinary([Row(notes: "=2+2")], ExportFormat.Xlsx);

        using var workbook = new XLWorkbook(new MemoryStream(file.Content));
        var sheet = workbook.Worksheet(1);
        var headers = sheet.Row(1).CellsUsed().Select(cell => cell.GetString()).ToArray();

        Assert.Equal(
            [
                "BusinessIp",
                "Location",
                "AliveStatus",
                "ComputerName",
                "SystemName",
                "OperatingSystemVersion",
                "DatabaseVersion",
                "Notes",
            ],
            headers);
        var notesCell = sheet.Cell(2, 8);
        Assert.Equal("=2+2", notesCell.GetString());
        Assert.True(notesCell.Style.IncludeQuotePrefix);
        Assert.False(notesCell.HasFormula);
        Assert.Equal(8, sheet.LastColumnUsed()!.ColumnNumber());
        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            file.ContentType);
        Assert.EndsWith(".xlsx", file.FileName, StringComparison.Ordinal);
    }

    [Fact]
    public void Password_xlsx_adds_only_sanitized_password_column()
    {
        var file = _writer.WritePasswords(
            [new PasswordExportRow(Row(), "=secret")]);

        using var workbook = new XLWorkbook(new MemoryStream(file.Content));
        var sheet = workbook.Worksheet(1);

        Assert.Equal("Password", sheet.Cell(1, 9).GetString());
        var passwordCell = sheet.Cell(2, 9);
        Assert.Equal("=secret", passwordCell.GetString());
        Assert.True(passwordCell.Style.IncludeQuotePrefix);
        Assert.False(passwordCell.HasFormula);
        Assert.Equal(9, sheet.LastColumnUsed()!.ColumnNumber());
    }

    [Fact]
    public void Password_xlsx_writes_empty_cell_when_secret_is_absent()
    {
        var file = _writer.WritePasswords(
            [new PasswordExportRow(Row(), null)]);

        using var workbook = new XLWorkbook(new MemoryStream(file.Content));

        Assert.True(workbook.Worksheet(1).Cell(2, 9).IsEmpty());
    }

    [Fact]
    public void Ordinary_csv_quotes_values_and_escapes_formula_prefixes()
    {
        var file = _writer.WriteOrdinary(
            [Row(location: "North, \"Rack\"", notes: "+SUM(A1:A2)")],
            ExportFormat.Csv);
        var csv = Encoding.UTF8.GetString(file.Content);

        Assert.StartsWith(
            "BusinessIp,Location,AliveStatus,ComputerName,SystemName,OperatingSystemVersion,DatabaseVersion,Notes\r\n",
            csv,
            StringComparison.Ordinal);
        Assert.Contains("\"North, \"\"Rack\"\"\"", csv, StringComparison.Ordinal);
        Assert.Contains("'+SUM(A1:A2)", csv, StringComparison.Ordinal);
        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.EndsWith(".csv", file.FileName, StringComparison.Ordinal);
    }

    private static ExportRow Row(
        string location = "DC",
        string? notes = null) =>
        new(
            "10.0.0.10",
            location,
            "Unknown",
            "server-10",
            "ERP",
            "Windows Server",
            "SQL Server",
            notes);
}
