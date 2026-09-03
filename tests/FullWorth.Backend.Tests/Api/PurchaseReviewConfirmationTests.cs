using System.Net;
using System.Net.Http.Json;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class PurchaseReviewConfirmationTests
{
    [Fact]
    public async Task DifferenceMustBeExplicitlyConfirmedBeforePurchaseCanBeConfirmed()
    {
        using var factory=new BackendWebApplicationFactory();using var client=factory.CreateClient();
        var owner=Guid.NewGuid();var purchaseId=Guid.NewGuid();await Seed(factory,owner,purchaseId,10m,9m);

        using(var request=UserRequest(HttpMethod.Post,$"/api/purchase-review/{purchaseId:D}/confirm?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",owner))
        using(var response=await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);

        using(var request=UserRequest(HttpMethod.Post,$"/api/purchase-review/{purchaseId:D}/confirm-difference?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",owner))
        using(var response=await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);

        using(var request=UserRequest(HttpMethod.Post,$"/api/purchase-review/{purchaseId:D}/confirm?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",owner))
        using(var response=await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.NoContent,response.StatusCode);

        await factory.SeedAsync(async db=>Assert.Equal("confirmed",(await db.Purchases.AsNoTracking().SingleAsync(x=>x.Id==purchaseId)).Status));
    }

    [Fact]
    public async Task ItemChangeInvalidatesPreviousDifferenceConfirmation()
    {
        using var factory=new BackendWebApplicationFactory();using var client=factory.CreateClient();
        var owner=Guid.NewGuid();var purchaseId=Guid.NewGuid();await Seed(factory,owner,purchaseId,10m,9m);

        using(var request=UserRequest(HttpMethod.Post,$"/api/purchase-review/{purchaseId:D}/confirm-difference?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",owner))
        using(var response=await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);

        using(var request=UserRequest(HttpMethod.Put,$"/api/purchases/{purchaseId:D}/items?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",owner))
        {
            request.Content=JsonContent.Create(new[]{new{categoryId=(Guid?)null,name="Changed item",brand=(string?)null,sku=(string?)null,asin=(string?)null,quantity=1m,unitPrice=(decimal?)null,totalPrice=8m,currency="EUR",notes=(string?)null}});
            using var response=await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NoContent,response.StatusCode);
        }

        using(var stateRequest=UserRequest(HttpMethod.Get,$"/api/purchase-review/{purchaseId:D}?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",owner))
        using(var stateResponse=await client.SendAsync(stateRequest))
        {
            Assert.Equal(HttpStatusCode.OK,stateResponse.StatusCode);
            var state=await stateResponse.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            Assert.False(state.GetProperty("differenceConfirmed").GetBoolean());
        }

        using(var request=UserRequest(HttpMethod.Post,$"/api/purchase-review/{purchaseId:D}/confirm?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",owner))
        using(var response=await client.SendAsync(request))
            Assert.Equal(HttpStatusCode.Conflict,response.StatusCode);
    }

    private static async Task Seed(BackendWebApplicationFactory factory,Guid owner,Guid purchaseId,decimal purchaseTotal,decimal itemTotal)
    {
        await factory.SeedAsync(async db=>
        {
            db.Users.Add(new FullWorthUser{Id=owner,EmailNormalized=$"{owner:N}@EXAMPLE.COM",DisplayName="Purchase review owner",IsActive=true});
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember{FullWorthSpaceId=FullWorthSpaceDefaults.LegacyId,UserId=owner,Role=FullWorthSpaceRoles.Owner});
            var purchase=new Purchase{Id=purchaseId,FullWorthSpaceId=FullWorthSpaceDefaults.LegacyId,Merchant="Test Shop",TotalAmount=purchaseTotal,Currency="EUR",Status="review"};
            purchase.Items.Add(new PurchaseItem{Name="Item",Quantity=1,TotalPrice=itemTotal,Currency="EUR",CategorizationSource="none"});
            db.Purchases.Add(purchase);await db.SaveChangesAsync();
        });
    }

    private static HttpRequestMessage UserRequest(HttpMethod method,string path,Guid userId)
    {
        var request=new HttpRequestMessage(method,path);
        request.Headers.Add("X-FullWorth-Internal-Key",BackendWebApplicationFactory.InternalKey);
        request.Headers.Add("X-FullWorth-User-Id",userId.ToString("D"));
        return request;
    }
}
