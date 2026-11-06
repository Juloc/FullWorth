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
        Assert.Contains("if (armed) waiting++;", js);
    }

    /// <summary>
    /// Der lange Bon nahm nach dem ersten Stueck nichts mehr auf. Er hat die Naht zum LETZTEN STUECK gesucht,
    /// die gleich nach einer Aufnahme ausserhalb dessen liegt, was eine Suche zuverlaessig findet, und auf
    /// verrauschten Bildern unsicher blieb (nachgestellt: eine Naehgenauigkeit von 0,56 bei Ruhe, nie ein
    /// zweites Stueck). Jetzt wird die Fahrt von Livebild zu Livebild gemessen, in einem Fenster um den letzten
    /// Schritt - und das Ende des Bons entscheidet ein Zaehler, nicht das einzelne Aufnahmebild.
    /// </summary>
    [Fact]
    public void The_long_receipt_measures_the_travel_between_live_images()
    {
        var js = CameraJs();

        Assert.Contains("import { splitIntoPages, stitchStrips, verticalShift } from './receipt-stitch.js';", js);
        Assert.Contains("const measured = verticalShift(before, live,", js);
        Assert.DoesNotContain("alignPair", js);
    }

    [Fact]
    public void One_frame_without_a_bottom_edge_does_not_end_the_long_receipt()
    {
        var js = CameraJs();

        // Das Ende gilt erst nach einer Weile (Zaehler), und nur der Zaehler macht ein Stueck zum letzten.
        Assert.Contains("endTicks >= END_TICKS", js);
        Assert.Contains("captureStrip(found, live, true)", js);
        Assert.Contains("continuesDown: !last", js);
    }

    private string CameraJs()
    {
        var environment = factory.Services.GetRequiredService<IWebHostEnvironment>();
        return File.ReadAllText(Path.Combine(environment.WebRootPath, "pages", "receipts", "receipt-camera.js"));
    }
}
