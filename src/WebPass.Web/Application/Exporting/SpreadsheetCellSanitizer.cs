namespace WebPass.Web.Application.Exporting;

public static class SpreadsheetCellSanitizer
{
    public static string Sanitize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value[0] is '=' or '+' or '-' or '@'
            ? $"'{value}"
            : value;
    }
}
