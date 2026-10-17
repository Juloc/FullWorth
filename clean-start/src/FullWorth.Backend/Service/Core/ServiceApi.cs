using FullWorth.Backend.Service.Public.Status.V1;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Backend.Service.Core;

public static class ServiceApi
{
    public static IServiceCollection AddFullWorthServices(this IServiceCollection services)
    {
        services.AddSingleton<ReadFrameworkStatusV1>();
        return services;
    }

    public static IEndpointRouteBuilder MapFullWorthServices(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/v1/public/status", (ReadFrameworkStatusV1 service) => Results.Ok(service.Execute()));
        return endpoints;
    }
}
