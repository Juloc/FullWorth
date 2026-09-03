using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;

namespace FullWorth.Backend.Tests.Api;

public sealed class InvestmentImportNumberFormatTests
{
    [Fact]
    public async Task UploadParsesGermanAndInternationalDecimalFormatsIdentically()
    {
        using var factory=new BackendWebApplicationFactory();using var client=factory.CreateClient();var owner=Guid.NewGuid();
        await factory.SeedAsync(async db=>
        {
            db.Users.Add(new FullWorthUser{Id=owner,EmailNormalized=$"{owner:N}@EXAMPLE.COM",DisplayName="Import number owner",IsActive=true});
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember{FullWorthSpaceId=FullWorthSpaceDefaults.LegacyId,UserId=owner,Role=FullWorthSpaceRoles.Owner});
            await db.SaveChangesAsync();
        });

        const string csv="Datum;Typ;Wertpapier;Stück;Kurs;Betrag;Währung;ID\r\n"+
            "01.08.2026;Kauf;ETF A;1;\"12,50\";\"12,50\";EUR;a\r\n"+
            "02.08.2026;Kauf;ETF A;1;12.50;12.50;EUR;b\r\n"+
            "03.08.2026;Kauf;ETF A;1;\"1.234,56\";\"1.234,56\";EUR;c\r\n"+
            "04.08.2026;Kauf;ETF A;1;\"1,234.56\";\"1,234.56\";EUR;d\r\n";

        using var request=UserRequest(HttpMethod.Post,$"/api/investment-import/upload?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",owner);
        using var form=new MultipartFormDataContent();
        var file=new ByteArrayContent(Encoding.UTF8.GetBytes(csv));file.Headers.ContentType=new MediaTypeHeaderValue("text/csv");form.Add(file,"file","numbers.csv");
        form.Add(new StringContent(JsonSerializer.Serialize(new
        {
            tradeDate="Datum",tradeType="Typ",settlementDate=(string?)null,securityName="Wertpapier",isin=(string?)null,wkn=(string?)null,ticker=(string?)null,
            quantity="Stück",price="Kurs",grossAmount=(string?)null,amount="Betrag",currency="Währung",fees=(string?)null,taxes=(string?)null,withholdingTax=(string?)null,externalKey="ID"
        }),Encoding.UTF8,"application/json"),"mapping");
        request.Content=form;
        using var response=await client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        using var upload=JsonDocument.Parse(await response.Content.ReadAsStringAsync());var job=upload.RootElement.GetProperty("jobId").GetGuid();

        using var summaryRequest=UserRequest(HttpMethod.Get,$"/api/investment-import/jobs/{job:D}/summary?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",owner);
        using var summaryResponse=await client.SendAsync(summaryRequest);Assert.Equal(HttpStatusCode.OK,summaryResponse.StatusCode);
        using var summary=JsonDocument.Parse(await summaryResponse.Content.ReadAsStringAsync());
        var rows=summary.RootElement.GetProperty("preview").EnumerateArray().OrderBy(x=>x.GetProperty("rowNumber").GetInt32()).ToArray();
        Assert.Equal(4,rows.Length);
        Assert.Equal(12.50m,rows[0].GetProperty("price").GetDecimal());
        Assert.Equal(12.50m,rows[1].GetProperty("price").GetDecimal());
        Assert.Equal(1234.56m,rows[2].GetProperty("price").GetDecimal());
        Assert.Equal(1234.56m,rows[3].GetProperty("price").GetDecimal());
        Assert.All(rows,row=>Assert.Equal(row.GetProperty("price").GetDecimal(),row.GetProperty("amount").GetDecimal()));
    }

    private static HttpRequestMessage UserRequest(HttpMethod method,string path,Guid userId)
    {
        var request=new HttpRequestMessage(method,path);
        request.Headers.Add("X-FullWorth-Internal-Key",BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id",userId.ToString("D"));
        return request;
    }
}
