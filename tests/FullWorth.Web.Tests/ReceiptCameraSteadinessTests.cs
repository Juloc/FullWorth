using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace FullWorth.Web.Tests;

/// <summary>
/// Die Belegkamera wartete mit ruhiger Hand ewig auf ein "ruhiges" Bild. Gemessen wurde die Bewegung als
/// mittlere Abweichung auf voller Analysebreite; Sensorrauschen und Handzittern lagen bei einem Beleg voller
/// Schrift staendig darueber (nachgestellt mit verrauschtem, zitterndem Kamerabild: zwoelf Sekunden
/// "Ruhig halten" und kein Foto). Zwei Dinge halten das fest.
/// </summary>
public sealed class ReceiptCameraSteadinessTests : IClassFixture<FullWorthWebFactory>
{
    private readonly FullWorthWebFactory factory;

    public ReceiptCameraSteadinessTests(FullWorthWebFactory factory) => this.factory = factory;

    [Fact]
    public void Motion_is_measured_on_a_coarse_image_so_noise_and_tremor_average_out()
    {
        var js = CameraJs();

        Assert.Contains("const coarse = toGray(rgba, width, height, width / MOTION_DIVISOR).gray;", js);
        Assert.Contains("motion(previousGray, coarse)", js);
    }

    [Fact]
    public void A_waiting_single_photo_loosens_its_limits_instead_of_waiting_forever()
    {
        var js = CameraJs();

        Assert.Contains("const patience = Math.min(waiting / PATIENCE_FRAMES, 1);", js);
        Assert.Contains("STEADY_MOTION * loosen", js);
        Assert.Contains("STEADY_SHIFT * loosen", js);
        // Beim langen Bon entscheidet "ruhig" ueber die Schaerfe jedes Stuecks - dort wird nichts gelockert.
        Assert.Contains("if (armed && mode === 'single') waiting++;", js);
    }

    private string CameraJs()
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(environment.WebRootPath, "pages", "receipts", "receipt-camera.js"));
    }
}
