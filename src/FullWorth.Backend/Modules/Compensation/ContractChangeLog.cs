using System.Globalization;

namespace FullWorth.Backend.Modules.Compensation;

/// <summary>
/// Das Aenderungsprotokoll: jeder bestaetigte Stand gegen den davor. Gespeichert wird nur der Stand -
/// "was hat sich geaendert" ist immer ein Rechenergebnis, und das Vorher ist der vorherige bestaetigte Stand,
/// nie etwas, das jemand eingetippt hat. Neueste zuerst.
/// </summary>
public static class ContractChangeLog
{
    private static readonly CultureInfo German = CultureInfo.GetCultureInfo("de-DE");

    public static IReadOnlyList<ContractEvent> Build(IReadOnlyList<ContractStateView> states)
    {
        var ordered = states.OrderBy(x => x.State.ValidFrom).ThenBy(x => x.ConfirmedAt).ToList();
        var events = new List<ContractEvent>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
            events.Add(EventFor(ordered[i], i == 0 ? null : ordered[i - 1].State));
        events.Reverse();
        return events;
    }

    private static ContractEvent EventFor(ContractStateView view, ContractStateWrite? before)
    {
        var now = Fields(view.State);
        var then = before is null ? null : Fields(before).ToDictionary(x => x.Key, x => x.Value);
        var changed = new List<ContractFieldChange>();
        var unchanged = new List<ContractFieldValue>();
        foreach (var (key, label, value) in now)
        {
            var old = then is not null && then.TryGetValue(key, out var previous) ? previous : null;
            if (then is not null && old == value) { if (value is not null) unchanged.Add(new(key, label, value)); }
            else if (value is not null || old is not null) changed.Add(new(key, label, old, value, key == "gross" ? Percent(before, view.State) : null));
        }
        // Ein Feld, das es im vorherigen Stand gab und jetzt nicht mehr, steht als geaendert da (Neu: "—").
        if (then is not null)
            foreach (var (key, value) in then.Where(x => x.Value is not null && now.All(n => n.Key != x.Key)))
                changed.Add(new(key, key.StartsWith("extra:", StringComparison.Ordinal) ? key[6..] : key, value, null, null));

        return new ContractEvent(view.Id, view.State.ValidFrom, view.State.Kind, Badge(view.State, before, changed),
            Title(view.State, before), changed, unchanged, view.ConfirmedAt, view.Document);
    }

    private static string Badge(ContractStateWrite state, ContractStateWrite? before, IReadOnlyList<ContractFieldChange> changed)
    {
        if (before is null) return "Start";
        if (state.GrossMonthly > before.GrossMonthly) return "Gehaltserhöhung";
        if (state.GrossMonthly < before.GrossMonthly) return "Gehaltsänderung";
        if (changed.Any(x => x.Key is "hours" or "vacation")) return "Arbeitszeit";
        if (changed.Any(x => x.Key is "car" or "bav")) return "Benefits";
        return state.Kind == ContractKinds.Addendum ? "Nachtrag" : "Änderung";
    }

    private static string Title(ContractStateWrite state, ContractStateWrite? before)
    {
        if (before is null) return state.Kind == ContractKinds.Addendum ? "Nachtrag" : string.IsNullOrWhiteSpace(state.Employer) ? "Arbeitsvertrag" : $"Arbeitsvertrag {state.Employer}";
        return state.Kind == ContractKinds.Addendum ? "Nachtrag zum Arbeitsvertrag" : "Neuer Arbeitsvertrag";
    }

    private static decimal? Percent(ContractStateWrite? before, ContractStateWrite now) =>
        before is { GrossMonthly: > 0 } ? Math.Round((now.GrossMonthly - before.GrossMonthly) / before.GrossMonthly * 100m, 1) : null;

    // Die Felder in der Reihenfolge, in der sie angezeigt werden; null heisst "nicht angegeben".
    private static List<(string Key, string Label, string? Value)> Fields(ContractStateWrite s)
    {
        var fields = new List<(string, string, string?)>
        {
            ("gross", "Bruttogehalt / Monat", Money(s.GrossMonthly)),
            ("hours", "Wochenstunden", s.WeeklyHours is { } h ? $"{Number(h)} h" : null),
            ("vacation", "Urlaubstage", s.VacationDays is { } v ? $"{Number(v)} Tage" : null),
            ("car", "Dienstwagen", s.CompanyCar ? "ja" : "nein"),
            ("bav", "bAV Arbeitgeberanteil", s.BavEmployerShare is { } b ? Money(b) : null),
            ("notice", "Kündigungsfrist", Clean(s.NoticePeriod)),
            ("employer", "Arbeitgeber", Clean(s.Employer)),
            ("position", "Position", Clean(s.Position))
        };
        foreach (var extra in s.Extras ?? []) fields.Add(($"extra:{extra.Label}", extra.Label, Clean(extra.Value)));
        return fields;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Money(decimal value) => value.ToString("N2", German) + " €";
    private static string Number(decimal value) => value.ToString("0.##", German);
}
