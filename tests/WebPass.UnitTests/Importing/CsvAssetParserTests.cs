using System.Text;
using WebPass.Web.Infrastructure.Importing;
using Xunit;

namespace WebPass.UnitTests.Importing;

public sealed class CsvAssetParserTests
{
    [Fact]
    public async Task Quoted_csv_row_is_parsed_without_losing_password_or_notes()
    {
        const string csv =
            "BusinessIp,Location,AliveStatus,ComputerName,SystemName,OperatingSystemVersion,DatabaseVersion,Notes,Password\r\n" +
            "10.0.0.10,\"DC, East\",Alive,server-10,ERP,Linux,SQL,\"note, one\",\"p,a,s,s\"\r\n";
        await using var source = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        var parser = new CsvAssetParser();

        var rows = new List<ImportSourceRow>();
        await foreach (var row in parser.ParseAsync(source, default))
        {
            rows.Add(row);
        }

        var parsed = Assert.Single(rows);
        Assert.Equal(2, parsed.RowNumber);
        Assert.Equal("DC, East", parsed.Location);
        Assert.Equal("note, one", parsed.Notes);
        Assert.Equal("p,a,s,s", parsed.Password);
    }
}
