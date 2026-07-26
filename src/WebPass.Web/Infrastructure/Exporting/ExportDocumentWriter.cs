using System.Text;
using ClosedXML.Excel;
using WebPass.Web.Application.Exporting;

namespace WebPass.Web.Infrastructure.Exporting;

public sealed class ExportDocumentWriter
{
    private static readonly string[] OrdinaryHeaders =
    [
        "BusinessIp",
        "Location",
        "AliveStatus",
        "ComputerName",
        "SystemName",
        "OperatingSystemVersion",
        "DatabaseVersion",
        "Notes",
    ];

    public ExportFile WriteOrdinary(
        IReadOnlyList<ExportRow> rows,
        ExportFormat format) =>
        format switch
        {
            ExportFormat.Csv => WriteCsv(rows),
            ExportFormat.Xlsx => WriteXlsx(
                rows.Select(row => Values(row)).ToArray(),
                OrdinaryHeaders,
                "webpass-servers"),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };

    public ExportFile WritePasswords(
        IReadOnlyList<PasswordExportRow> rows)
    {
        var headers = OrdinaryHeaders.Append("Password").ToArray();
        var values = rows
            .Select(row => Values(row.Asset).Append(row.Password).ToArray())
            .ToArray();
        return WriteXlsx(values, headers, "webpass-server-passwords");
    }

    private static ExportFile WriteCsv(IReadOnlyList<ExportRow> rows)
    {
        var csv = new StringBuilder();
        csv.AppendJoin(',', OrdinaryHeaders).Append("\r\n");
        foreach (var row in rows)
        {
            csv.AppendJoin(
                ',',
                Values(row)
                    .Select(SpreadsheetCellSanitizer.Sanitize)
                    .Select(QuoteCsv));
            csv.Append("\r\n");
        }

        return new ExportFile(
            new UTF8Encoding(false).GetBytes(csv.ToString()),
            "text/csv; charset=utf-8",
            FileName("webpass-servers", "csv"));
    }

    private static ExportFile WriteXlsx(
        IReadOnlyList<IReadOnlyList<string?>> rows,
        IReadOnlyList<string> headers,
        string fileNamePrefix)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Servers");
        for (var column = 0; column < headers.Count; column++)
        {
            sheet.Cell(1, column + 1).Value = headers[column];
        }

        for (var row = 0; row < rows.Count; row++)
        {
            for (var column = 0; column < headers.Count; column++)
            {
                var value = SpreadsheetCellSanitizer.Sanitize(rows[row][column]);
                if (value.Length != 0)
                {
                    sheet.Cell(row + 2, column + 1).Value = value;
                }
            }
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return new ExportFile(
            stream.ToArray(),
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            FileName(fileNamePrefix, "xlsx"));
    }

    private static string?[] Values(ExportRow row) =>
    [
        row.BusinessIp,
        row.Location,
        row.AliveStatus,
        row.ComputerName,
        row.SystemName,
        row.OperatingSystemVersion,
        row.DatabaseVersion,
        row.Notes,
    ];

    private static string QuoteCsv(string value)
    {
        if (!value.Contains(',')
            && !value.Contains('"')
            && !value.Contains('\r')
            && !value.Contains('\n'))
        {
            return value;
        }

        return $"\"{value.Replace("\"", "\"\"")}\"";
    }

    private static string FileName(string prefix, string extension) =>
        $"{prefix}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}.{extension}";
}
