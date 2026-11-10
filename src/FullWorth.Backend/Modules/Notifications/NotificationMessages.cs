using FullWorth.Backend.Modules.Push;

namespace FullWorth.Backend.Modules.Notifications;

/// <summary>
/// Builds push payloads. Copy is German because the backend currently has no per-recipient locale;
/// the web notification settings remain bilingual. Purchase messages intentionally avoid exposing
/// amounts in the lock-screen notification body; booking messages show them only when the user allowed it.
/// </summary>
/// <summary>A booking as a notification line. The name is what the bank calls the other side.</summary>
public sealed record NewBookingLine(string Name, decimal Amount, string Currency);

public static class NotificationMessages
{
    public static PushMessage BankReauth(string institution) =>
        new("Bank-Neuanmeldung erforderlich", $"{institution} muss neu verbunden werden.", "/accounts");

    public static PushMessage BankSyncError(string institution) =>
        new("Fehler beim Bankabgleich", $"{institution} konnte nicht abgeglichen werden.", "/accounts");

    public static PushMessage BudgetNear(string name, int percent) =>
        new("Budget nahe am Limit", $"{name}: {percent}% verbraucht.", "/budgets");

    public static PushMessage BudgetOver(string name, int percent) =>
        new("Budget-Limit überschritten", $"{name}: {percent}% verbraucht.", "/budgets");

    public static PushMessage ContractDue(string name, DateOnly dueDate) =>
        new("Vertrag bald fällig", $"{name} ist am {dueDate:dd.MM.yyyy} fällig.", "/contracts");

    public static PushMessage PurchaseReview(string merchant) =>
        new("Kauf prüfen", $"Ein Kauf bei {SafeName(merchant)} wartet auf deine Prüfung.", "/purchases");

    public static PushMessage PurchaseScanFailed() =>
        new("Beleg konnte nicht gelesen werden", "Der Beleg ist gespeichert. Du kannst die Erkennung erneut starten oder die Daten manuell prüfen.", "/purchases");

    public static PushMessage PurchaseUnmatched(string merchant) =>
        new("Beleg noch nicht verknüpft", $"Der Kauf bei {SafeName(merchant)} hat noch keine passende Zahlung.", "/purchases");

    public static PushMessage PurchaseReturnDeadline(string itemName, DateOnly deadline) =>
        new("Rückgabefrist endet bald", $"{SafeName(itemName)}: Rückgabe bis {deadline:dd.MM.yyyy}.", "/purchases");

    public static PushMessage PurchaseWarrantyDeadline(string itemName, DateOnly deadline) =>
        new("Garantie endet bald", $"{SafeName(itemName)}: Garantie bis {deadline:dd.MM.yyyy}.", "/purchases");

    /// <summary>One notification for everything a fetch brought in: the booking itself, or a short list.</summary>
    public static PushMessage NewBookings(IReadOnlyList<NewBookingLine> bookings, bool showAmounts)
    {
        string Line(NewBookingLine booking) =>
            showAmounts ? $"{SafeName(booking.Name)} {FormatAmount(booking.Amount, booking.Currency)}" : SafeName(booking.Name);

        if (bookings.Count == 1) return new("Neue Buchung", Line(bookings[0]), "/transactions");

        const int Shown = 3;
        var lines = bookings.Take(Shown).Select(Line).ToList();
        if (bookings.Count > Shown) lines.Add($"+ {bookings.Count - Shown} weitere");
        return new($"{bookings.Count} neue Buchungen", string.Join('\n', lines), "/transactions");
    }

    private static string FormatAmount(decimal amount, string currency)
    {
        var german = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        var sign = amount < 0 ? "\u2212" : "+";
        var symbol = string.Equals(currency, "EUR", StringComparison.OrdinalIgnoreCase) ? "\u20AC" : currency;
        return $"{sign}{Math.Abs(amount).ToString("N2", german)} {symbol}";
    }

    private static string SafeName(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "deinem Kauf" : value.Trim();
        return text.Length <= 80 ? text : text[..80] + "…";
    }
}
