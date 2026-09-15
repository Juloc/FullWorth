
namespace FullWorth.Backend.Modules.Ingestion;

public static class IngestionEndpoints
{
    public static IEndpointRouteBuilder MapIngestionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/internal/banking/ingest", async (FinanceIngestBatch batch, IngestionService service, CancellationToken ct) => Results.Ok(await service.IngestAsync(batch, ct))).WithTags("Internal banking");
        return app;
    }
}
