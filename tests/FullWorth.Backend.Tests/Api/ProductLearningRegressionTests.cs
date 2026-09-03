using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FullWorth.Backend.Modules.Categories;
using FullWorth.Backend.Modules.FullWorthSpaces;
using FullWorth.Backend.Modules.Purchases;
using FullWorth.Backend.Modules.Users;
using FullWorth.Backend.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Tests.Api;

public sealed class ProductLearningRegressionTests
{
    // The former ConfirmedAliasPrefillsUncategorizedItemAndCreatesProductLink and
    // ProductAliasNeverOverridesExplicitManualCategory tests exercised the legacy ProductIdentities
    // DB-trigger that auto-prefilled/linked purchase items on raw INSERT. That trigger and its tables
    // (ProductIdentities/ProductIdentityAliases/PurchaseItemProductLinks) were intentionally removed by
    // the Products/Articles unification migration; product learning is now an explicit, API-driven flow
    // covered by ThreeManualChoicesProduceSuggestionAndAcceptanceCreatesLearnedProduct below.

    [Fact]
    public async Task ThreeManualChoicesProduceSuggestionAndAcceptanceCreatesLearnedProduct()
    {
        using var factory=new BackendWebApplicationFactory();using var client=factory.CreateClient();
        var owner=Guid.NewGuid();var category=Guid.NewGuid();await SeedOwner(factory,owner);
        await factory.SeedAsync(async db=>
        {
            db.Categories.Add(new FinanceCategory{Id=category,FullWorthSpaceId=FullWorthSpaceDefaults.LegacyId,Key=$"test.{category:N}",Name="Snacks"});
            for(var i=0;i<3;i++)
            {
                var purchase=new Purchase{FullWorthSpaceId=FullWorthSpaceDefaults.LegacyId,Merchant="REWE",Currency="EUR",Status="confirmed"};
                purchase.Items.Add(new PurchaseItem{Name="Protein Bar Chocolate",CategoryId=category,CategorizationSource="manual",Quantity=1,TotalPrice=1.99m,Currency="EUR"});
                db.Purchases.Add(purchase);
            }
            await db.SaveChangesAsync();
        });

        using(var request=UserRequest(HttpMethod.Get,$"/api/product-learning/category-suggestions?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",owner))
        using(var response=await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var suggestion=Assert.Single(document.RootElement.EnumerateArray());
            Assert.Equal(category,suggestion.GetProperty("categoryId").GetGuid());
            Assert.Equal(3,suggestion.GetProperty("count").GetInt32());
        }

        using(var request=UserRequest(HttpMethod.Post,$"/api/product-learning/category-suggestions/accept?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",owner))
        {
            request.Content=JsonContent.Create(new{text="Protein Bar Chocolate",categoryId=category,canonicalName="Protein Bar Chocolate"});
            using var response=await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        }

        using(var request=UserRequest(HttpMethod.Get,$"/api/product-learning/category-suggestions?fullWorthSpaceId={FullWorthSpaceDefaults.LegacyId:D}",owner))
        using(var response=await client.SendAsync(request))
        {
            Assert.Equal(HttpStatusCode.OK,response.StatusCode);
            using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Empty(document.RootElement.EnumerateArray());
        }
    }

    private static async Task SeedOwner(BackendWebApplicationFactory factory,Guid userId)
    {
        await factory.SeedAsync(async db=>
        {
            db.Users.Add(new FullWorthUser{Id=userId,EmailNormalized=$"{userId:N}@EXAMPLE.COM",DisplayName="Product learning owner",IsActive=true});
            db.FullWorthSpaceMembers.Add(new FullWorthSpaceMember{FullWorthSpaceId=FullWorthSpaceDefaults.LegacyId,UserId=userId,Role=FullWorthSpaceRoles.Owner});
            await db.SaveChangesAsync();
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
