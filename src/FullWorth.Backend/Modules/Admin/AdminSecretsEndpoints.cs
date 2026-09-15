using System.Text.Json;
using FullWorth.Backend.Security;
using FullWorth.Shared;
using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Admin;

/// <summary>
/// The finance side of the admin vault: the credentials a person entered themselves and which are
/// encrypted with <see cref="FieldCipher"/> in this database.
///
/// Three gates, and none of them alone is enough:
///
/// <list type="number">
/// <item>The ingest key, which the <c>/internal</c> middleware already demands.</item>
/// <item>A signed <see cref="AdminVaultTicket"/>, minted by the BFF only after the step-up. It is
/// signed with a key DERIVED from the internal key, not the internal key itself — otherwise the
/// internal key would silently become a read-anything key.</item>
/// <item>In the unified host, a loopback check. These routes exist for one caller in the same
/// container; nothing should be able to reach them across a Docker network even holding both keys.</item>
/// </list>
///
/// And one rule above the gates: <b>the ticket names a finance user, and only that user's own rows
/// are ever read.</b> An administrator is not entitled to somebody else's bank PIN. Every query here
/// filters on the recorded owner, which is why space-scoped tables are matched on the user who set
/// the connection up rather than on the space.
/// </summary>
public static class AdminSecretsEndpoints
{
    private const string BankGroup = "Bankzugänge";
    private const string AiGroup = "KI-Zugänge";
    private const string ImportGroup = "Importe";

    public static IEndpointRouteBuilder MapAdminSecretsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/internal/admin/secrets").WithTags("Internal admin");

        group.MapPost("/inventory", async (
            HttpContext context,
            AdminSecretsStore secrets,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            if (!Authorised(context, configuration, out var userId)) return Results.Unauthorized();
            NoStore(context);
            return Results.Ok(await secrets.ListAsync(userId, ct));
        });

        group.MapPost("/reveal", async (
            AdminSecretRevealRequest request,
            HttpContext context,
            AdminSecretsStore secrets,
            FieldCipher cipher,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            if (!Authorised(context, configuration, out var userId)) return Results.Unauthorized();
            NoStore(context);

            var value = await secrets.RevealAsync(cipher, userId, request.Reference, ct);
            return value is null
                ? Results.NotFound(new { error = "not_set" })
                : Results.Ok(new { value });
        });

        return app;
    }

    public sealed record AdminSecretRevealRequest(string? Reference);

    // No TimeProvider parameter: this project does not register one, and the minimal-API binder would
    // fail to infer it at route-build time rather than at call time - the whole backend stops routing.
    private static bool Authorised(
        HttpContext context, IConfiguration configuration, out Guid financeUserId)
    {
        financeUserId = Guid.Empty;

        // Gate 3. In the unified host the BFF and the backend share a process and talk over loopback,
        // so anything arriving from elsewhere is by definition not the caller these routes exist for.
        //
        // The key is read here rather than through Shared/UnifiedHost.cs, which this project does not
        // link. One key, read the same way the image sets it.
        if (bool.TryParse(configuration["FullWorthHost:Unified"], out var unified) && unified)
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote is null || !System.Net.IPAddress.IsLoopback(remote)) return false;
        }

        // Gate 2. The ingest key (gate 1) was already checked by the /internal middleware.
        return AdminVaultTicket.TryValidate(
            configuration["Security:InternalKey"],
            context.Request.Headers[AdminVaultTicket.HeaderName],
            DateTimeOffset.UtcNow,
            out financeUserId);
    }

    private static void NoStore(HttpContext context) =>
        context.Response.Headers.CacheControl = "no-store, max-age=0";
}
