using FullWorth.Backend.Modules.Push;

namespace FullWorth.Backend.Modules.Notifications;

/// <summary>
/// Builds push payloads. Copy is German because the backend currently has no per-recipient locale;
/// the web notification settings remain bilingual. Purchase messages intentionally avoid exposing
/// amounts in the lock-screen notification body.
/// </summary>
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

    private static string SafeName(string? value)
    {
        var text = string.IsNullOrWhiteSpace(value) ? "deinem Kauf" : value.Trim();
        return text.Length <= 80 ? text : text[..80] + "…";
    }
}
