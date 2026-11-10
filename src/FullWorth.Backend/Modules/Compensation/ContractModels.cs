namespace FullWorth.Backend.Modules.Compensation;

public static class ContractKinds
{
    public const string Contract = "contract";
    public const string Addendum = "addendum";
}

/// <summary>Ein weiteres benanntes Feld des Vertrags, das die Auslesung sicher geliefert hat (Probezeit, Standort, ...).</summary>
public sealed record ContractField(string Label, string Value);

/// <summary>Was die Auslesung eines Vertrags oder Nachtrags liefert - jedes Feld darf fehlen, erfunden wird keins.</summary>
public sealed record ContractExtractionResult(
    DateOnly? ValidFrom,
    string? Kind,
    string? Employer,
    decimal? GrossMonthly,
    decimal? WeeklyHours,
    decimal? VacationDays,
    bool? CompanyCar,
    decimal? BavEmployerShare,
    string? NoticePeriod,
    string? Position,
    IReadOnlyList<ContractField> Extras,
    decimal ConfidencePercent,
    IReadOnlyList<string> DetectedLabels,
    IReadOnlyList<string> Warnings);

/// <summary>Ein bestaetigter Vertragsstand: alle Werte, die ab <see cref="ValidFrom"/> gelten.</summary>
public sealed record ContractStateWrite(
    DateOnly ValidFrom,
    string Kind,
    decimal GrossMonthly,
    string? Employer = null,
    decimal? WeeklyHours = null,
    decimal? VacationDays = null,
    bool CompanyCar = false,
    decimal? BavEmployerShare = null,
    string? NoticePeriod = null,
    string? Position = null,
    IReadOnlyList<ContractField>? Extras = null,
    string? Note = null);

public sealed record ContractStateView(
    Guid Id,
    Guid FullWorthSpaceId,
    ContractStateWrite State,
    DateTimeOffset ConfirmedAt,
    DocumentRef? Document);

/// <summary>Ein Wert, der sich gegenueber dem vorherigen Stand geaendert hat; Prozent nur beim Gehalt.</summary>
public sealed record ContractFieldChange(string Key, string Label, string? Old, string? New, decimal? Percent);

public sealed record ContractFieldValue(string Key, string Label, string Value);

/// <summary>Ein Ereignis im Aenderungsprotokoll: ein Stand, gerechnet gegen den vorherigen bestaetigten.</summary>
public sealed record ContractEvent(
    Guid Id,
    DateOnly ValidFrom,
    string Kind,
    string Badge,
    string Title,
    IReadOnlyList<ContractFieldChange> Changed,
    IReadOnlyList<ContractFieldValue> Unchanged,
    DateTimeOffset ConfirmedAt,
    DocumentRef? Document);

/// <summary>Ein Hinweis aus dem Abgleich von Vertragsstaenden und Abrechnungen.</summary>
public sealed record ContractHint(string Kind, string Message, DateOnly? SuggestedValidFrom, decimal? SuggestedGross);

public sealed record ContractOverview(
    IReadOnlyList<ContractStateView> States,
    IReadOnlyList<ContractEvent> Events,
    IReadOnlyList<ContractHint> Hints);
