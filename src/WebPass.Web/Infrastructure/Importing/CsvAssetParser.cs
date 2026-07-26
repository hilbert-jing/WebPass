using System.Runtime.CompilerServices;
using Microsoft.VisualBasic.FileIO;

namespace WebPass.Web.Infrastructure.Importing;

public sealed record ImportSourceRow(
    int RowNumber,
    string BusinessIp,
    string Location,
    string AliveStatus,
    string ComputerName,
    string SystemName,
    string? OperatingSystemVersion,
    string? DatabaseVersion,
    string? Notes,
    string? Password);

public interface IAssetImportParser
{
    IAsyncEnumerable<ImportSourceRow> ParseAsync(
        Stream source,
        CancellationToken ct);
}

public sealed class CsvAssetParser : IAssetImportParser
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
        using var parser = new TextFieldParser(source)
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
        };
        parser.SetDelimiters(",");

        var header = parser.ReadFields();
        if (header is null
            || header.Length != Headers.Length
            || !header.SequenceEqual(Headers, StringComparer.OrdinalIgnoreCase))
        {
            throw new FormatException("The import header is invalid.");
        }

        var rowNumber = 1;
        while (!parser.EndOfData)
        {
            ct.ThrowIfCancellationRequested();
            rowNumber++;
            string[]? fields;
            try
            {
                fields = parser.ReadFields();
            }
            catch (MalformedLineException)
            {
                throw new FormatException($"Import row {rowNumber} is malformed.");
            }

            if (fields is null || fields.All(string.IsNullOrEmpty))
            {
                continue;
            }
            if (fields.Length != Headers.Length)
            {
                throw new FormatException($"Import row {rowNumber} has an invalid column count.");
            }

            yield return new ImportSourceRow(
                rowNumber,
                fields[0],
                fields[1],
                fields[2],
                fields[3],
                fields[4],
                EmptyToNull(fields[5]),
                EmptyToNull(fields[6]),
                EmptyToNull(fields[7]),
                EmptyToNull(fields[8]));
            await Task.Yield();
        }
    }

    private static string? EmptyToNull(string value) =>
        value.Length == 0 ? null : value;
}
