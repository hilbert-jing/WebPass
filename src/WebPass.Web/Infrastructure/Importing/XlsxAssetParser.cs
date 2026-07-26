using System.Runtime.CompilerServices;
using ClosedXML.Excel;

namespace WebPass.Web.Infrastructure.Importing;

public sealed class XlsxAssetParser : IAssetImportParser
{
    private static readonly string[] Headers =
    [
        "BusinessIp",
        "Location",
        "AliveStatus",
        "ComputerName",
        "SystemName",
        "OperatingSystemVersion",
        "DatabaseVersion",
        "Notes",
        "Password",
    ];

    public async IAsyncEnumerable<ImportSourceRow> ParseAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken ct)
    {
        using var workbook = new XLWorkbook(source);
        var sheet = workbook.Worksheets.FirstOrDefault()
            ?? throw new FormatException("The workbook has no worksheet.");
        var lastColumn = sheet.LastColumnUsed()?.ColumnNumber() ?? 0;
        if (lastColumn != Headers.Length)
        {
            throw new FormatException("The import header is invalid.");
        }

        for (var column = 1; column <= Headers.Length; column++)
        {
            var cell = sheet.Cell(1, column);
            if (cell.HasFormula
                || !StringComparer.OrdinalIgnoreCase.Equals(cell.GetString(), Headers[column - 1]))
            {
                throw new FormatException("The import header is invalid.");
            }
        }

        var lastRow = sheet.LastRowUsed()?.RowNumber() ?? 1;
        for (var rowNumber = 2; rowNumber <= lastRow; rowNumber++)
        {
            ct.ThrowIfCancellationRequested();
            var row = sheet.Row(rowNumber);
            var cells = Enumerable.Range(1, Headers.Length)
                .Select(column => row.Cell(column))
                .ToArray();
            if (cells.Any(cell => cell.HasFormula))
            {
                throw new FormatException($"Import row {rowNumber} contains a formula.");
            }
            if (cells.All(cell => cell.IsEmpty()))
            {
                continue;
            }

            var values = cells.Select(cell => cell.GetString()).ToArray();
            yield return new ImportSourceRow(
                rowNumber,
                values[0],
                values[1],
                values[2],
                values[3],
                values[4],
                EmptyToNull(values[5]),
                EmptyToNull(values[6]),
                EmptyToNull(values[7]),
                EmptyToNull(values[8]));
            await Task.Yield();
        }
    }

    private static string? EmptyToNull(string value) =>
        value.Length == 0 ? null : value;
}
