namespace FullWorth.Backend.Modules.Compensation;

/// <summary>Wofuer ein Wohnkostenbetrag steht. Gleiche Namen wie in der Oberflaeche.</summary>
public static class HousingCostKinds
{
    public const string Heating = "heating";
    public const string Water = "water";
    public const string Ancillary = "ancillary";
    public const string Electricity = "electricity";
    public const string Other = "other";

    public static bool IsKnown(string kind) => kind is Heating or Water or Ancillary or Electricity or Other;
}

/// <summary>Ein Betrag aus einer Abrechnung fuer einen Zeitraum, optional mit der Nummer des Paperless-Dokuments, aus dem er stammt.</summary>
public sealed record HousingCostWrite(
    string Kind,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    decimal Amount,
    string? Note = null,
    int? PaperlessDocumentId = null);

public sealed record HousingCostView(
    Guid Id,
    string Kind,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    decimal Amount,
    string? Note,
    int? PaperlessDocumentId,
    DateTimeOffset CreatedAt);
