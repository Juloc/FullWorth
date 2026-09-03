namespace FullWorth.Backend.Modules.Notifications;

/// <summary>
/// Notification type keys. These MUST match the frontend toggle keys and the per-user
/// "notifications.types" preference map so notifications remain independently opt-out.
/// </summary>
public static class NotificationTypes
{
    public const string BankReauth = "bank_reauth";
    public const string BankSyncError = "bank_sync_error";
    public const string ContractDue = "contract_due";
    public const string BudgetNear = "budget_near";
    public const string BudgetOver = "budget_over";
    public const string BackupFailed = "backup_failed";

    public const string PurchaseReview = "purchase_review";
    public const string PurchaseScanFailed = "purchase_scan_failed";
    public const string PurchaseUnmatched = "purchase_unmatched";
    public const string PurchaseReturnDeadline = "purchase_return_deadline";
    public const string PurchaseWarrantyDeadline = "purchase_warranty_deadline";

    public const string PropertyEnergyExpiry = "property_energy_expiry";
    public const string PropertyValuationStale = "property_valuation_stale";
}
