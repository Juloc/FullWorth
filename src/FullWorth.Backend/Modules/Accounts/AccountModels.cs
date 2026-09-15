using Microsoft.EntityFrameworkCore;

namespace FullWorth.Backend.Modules.Accounts;

public sealed class FinanceAccount
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    // Null for manual accounts (e.g. cash) that exist without any bank connection.
    public Guid? BankConnectionId { get; set; }
    public string Provider { get; set; } = "enable-banking";
    public string IdentificationHash { get; set; } = string.Empty;
    // Enable Banking may expose several equivalent hashes (e.g. IBAN/BBAN and legacy hash versions).
    // Keep the aliases so a later session can resolve the same account even when the primary hash changes.
    public string IdentificationHashesJson { get; set; } = "[]";
    public string ProviderAccountId { get; set; } = string.Empty;
    // Import archive accounts can be explicitly and persistently mapped to their canonical account.
    // Null for ordinary accounts and for imports that have not been confirmed by the user yet.
    // NOTE: this is a MERGE marker, not a counting one - FinanzguruAccountReconciliationService MOVES
    // the archive's bookings onto the target and keeps doing so on every sync. It is therefore not the
    // home for "these two accounts are the same"; that is DuplicateOfAccountId below, which never
    // touches a booking.
    public Guid? ImportLinkedAccountId { get; set; }
    public string InstitutionName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// Wie die Bank das Konto nennt. Bleibt als Herkunftsangabe erhalten und wird nie von einer
    /// Umbenennung ueberschrieben; <see cref="DisplayName"/> ist der Name, den der Benutzer sieht.
    ///
    /// Es gab die Spalte nicht, und deshalb entschied beim Sync eine Rateregel, ob der vorhandene
    /// Name ueberschrieben werden darf (GROSS_MIT_UNTERSTRICH = wohl vom Anbieter). Mit zwei Spalten
    /// ist es keine Frage mehr: der Sync schreibt hierhin, und DisplayName nur dann, wenn er noch
    /// gleich dem bisherigen Anbieternamen ist - also niemand ihn geaendert hat (#125).
    /// </summary>
    public string? ProviderDisplayName { get; set; }
    public string? Product { get; set; }
    public string? AccountType { get; set; }
    public string? Usage { get; set; }
    public string? PsuStatus { get; set; }
    public decimal? CreditLimitAmount { get; set; }
    public string? CreditLimitCurrency { get; set; }
    public string Currency { get; set; } = "EUR";
    public string? IbanLast4 { get; set; }
    // Keyed lookup token for exact transfer matching. The full IBAN is never persisted.
    public string? IbanLookup { get; set; }
    public bool IsActive { get; set; } = true;
    public bool IncludeInNetWorth { get; set; } = true;

    /// <summary>
    /// The account the owner declared this one to be the same real-world account as. Set only by an
    /// explicit user decision, never by a sync, and deliberately independent of the IBAN: PayPal, Wise,
    /// Revolut, cash and manual accounts have no IBAN, so every identity check keyed on that token
    /// (IbanLookup) can never see them as duplicates of anything.
    ///
    /// It is a statement about COUNTING, not a data merge: the linked account keeps every booking and
    /// balance it has and stays visible, it is only left out of the totals.
    /// </summary>
    public Guid? DuplicateOfAccountId { get; set; }

    /// <summary>
    /// What <see cref="IncludeInNetWorth"/> was immediately before the link was made, so unlinking
    /// restores the previous state instead of guessing "true". An account that the automatic IBAN rule
    /// had already excluded at creation goes back to excluded, not to counted.
    /// </summary>
    public bool? IncludeInNetWorthBeforeLink { get; set; }
    public int SortOrder { get; set; }
    // Optional user-defined group (§8.1). SetNull on group delete, so accounts are never orphaned.
    public Guid? GroupId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<AccountOwner> Owners { get; set; } = [];
}

/// <summary>A user-defined, reorderable group of accounts within a space (UI_UX_SPEC §8.1).</summary>
public sealed class AccountGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FullWorthSpaceId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int SortOrder { get; set; }

    /// <summary>
    /// Die Standardgruppe des Space. Es gibt genau eine, sie laesst sich nicht loeschen, und ein Konto
    /// ohne eigene Gruppe landet in ihr.
    ///
    /// Vorher konnte ein Konto gruppenlos sein, und die Oberflaeche zeigte dafuer einen Eimer "Ohne
    /// Gruppe", der keine Gruppe war: er liess sich nicht anklicken wie eine, denn es gibt keinen
    /// Serverfilter fuer "hat keine Gruppe". #125 verlangt, dass jede Gruppenzeile die Buchungen genau
    /// ihrer Konten oeffnet - also muss auch die Standardgruppe eine echte Gruppe sein.
    /// </summary>
    public bool IsDefault { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class BalanceSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "EUR";
    public string BalanceType { get; set; } = string.Empty;

    /// <summary>
    /// Where this figure came from - see <see cref="BalanceSources"/>. A balance said what it was (the
    /// bank's balance_type) but never where it came from, so a value the owner typed and a value a bank
    /// reported looked identical on screen. That matters most where there is no bank at all.
    /// </summary>
    public string? Source { get; set; }

    /// <summary>The owner's own remark, e.g. which statement or app the figure was read off.</summary>
    public string? Note { get; set; }

    /// <summary>The date the figure is valid FOR, as opposed to when it was recorded.</summary>
    public DateOnly? ReferenceDate { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>The provenance of a balance. Stored, never guessed from the balance type.</summary>
public static class BalanceSources
{
    /// <summary>Reported by a bank through a live connection.</summary>
    public const string Provider = "provider";

    /// <summary>Entered by the owner.</summary>
    public const string Manual = "manual";

    /// <summary>Read off an imported file (a statement, or an import wizard's anchor).</summary>
    public const string Import = "import";
}
