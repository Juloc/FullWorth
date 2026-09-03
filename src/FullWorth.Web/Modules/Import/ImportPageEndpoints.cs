namespace FullWorth.Web.Modules.Import;

/// <summary>
/// Registers the protected import pages in one place. FullWorth.Web currently calls the historic
/// Finanzguru registration method from Program.cs; that method delegates here so adding another
/// import source never requires provider-to-provider route coupling.
/// </summary>
public static class ImportPageEndpoints
{
    public static IEndpointRouteBuilder MapImportPageEndpoints(this IEndpointRouteBuilder app)
    {
        ImportCenterPageEndpoints.MapImportCenterPageEndpoints(app);
        FinanzguruImportPageEndpoints.MapFinanzguruProviderPageEndpoints(app);
        BrokerPdfImportPageEndpoints.MapBrokerPdfImportPageEndpoints(app);
        return app;
    }
}
