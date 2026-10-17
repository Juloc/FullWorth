using FullWorth.Backend.Data.Public.Status.V1;
using FullWorth.Backend.Service.Public.Status.V1;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace FullWorth.Frontend.Pages;

public sealed class IndexModel : PageModel
{
    private readonly ReadFrameworkStatusV1 _readFrameworkStatus;

    public IndexModel(ReadFrameworkStatusV1 readFrameworkStatus)
    {
        _readFrameworkStatus = readFrameworkStatus;
    }

    public FrameworkStatusV1 Status { get; private set; } = null!;

    public void OnGet()
    {
        Status = _readFrameworkStatus.Execute();
    }
}
