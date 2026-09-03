using System.Globalization;
using System.Text.RegularExpressions;

namespace FullWorth.Backend.Modules.Coach;

public sealed class DeterministicCoachEngine
{
    private enum Intent
    {
        WhereMoneyWent,
        WhatChanged,
        WhatToReduce,
        RegrettedSpending,
        WorthwhileSpending,
        TargetDate,
        FinancialIndependence,
        Affordability,
        MonthlySummary,
        General
    }

    public CoachAnswer Answer(string question, CoachContext context)
    {
        var de = IsGerman(question);
        return Classify(question) switch
        {
            Intent.WhereMoneyWent => WhereMoneyWent(context, de),
            Intent.WhatChanged => WhatChanged(context, de),
            Intent.WhatToReduce => WhatToReduce(context, de),
            Intent.RegrettedSpending => Regretted(context, de),
            Intent.WorthwhileSpending => Worthwhile(context, de),
            Intent.TargetDate => TargetDate(question, context, de),
            Intent.FinancialIndependence => FinancialIndependence(question, context, de),
            Intent.Affordability => Affordability(question, context, de),
            Intent.MonthlySummary => MonthlySummary(context, de),
            _ => General(context, de)
        };
    }

    public IReadOnlyList<CoachTargetScenario> BuildTargetScenarios(CoachContext context, decimal? annualReturn = null) =>
        new[] { 100_000m, 250_000m, 500_000m, 1_000_000m }
            .Select(target => ProjectTarget(target, context.CurrentNetWorth, context.AverageMonthlySavings, annualReturn)).ToList();

    private static CoachTargetScenario ProjectTarget(decimal target, decimal? current, decimal? monthlySavings, decimal? annualReturn)
    {
        if (!current.HasValue || !monthlySavings.HasValue) return new(target, null, null, current ?? 0m, monthlySavings ?? 0m, annualReturn);
        if (current.Value >= target) return new(target, DateOnly.FromDateTime(DateTime.UtcNow), 0, current.Value, monthlySavings.Value, annualReturn);
        if (monthlySavings.Value <= 0m) return new(target, null, null, current.Value, monthlySavings.Value, annualReturn);

        var balance = current.Value;
        var months = 0;
        var monthlyRate = annualReturn is > 0m ? (decimal)Math.Pow(1d + (double)annualReturn.Value, 1d / 12d) - 1m : 0m;
        while (balance < target && months < 1200)
        {
            balance = balance * (1m + monthlyRate) + monthlySavings.Value;
            months++;
        }
        DateOnly? date = months >= 1200 ? null : DateOnly.FromDateTime(DateTime.UtcNow).AddMonths(months);
        return new(target, date, date.HasValue ? months : null, current.Value, monthlySavings.Value, annualReturn);
    }

    private static Intent Classify(string question)
    {
        var q = question.ToLowerInvariant();
        if (Has(q, "financially independent", "financial independence", "finanziell unabhängig", "finanzielle unabhängigkeit", "finanzielle unabhaengigkeit"))
            return Intent.FinancialIndependence;
        if (Has(q, "reich", "100k", "100.000", "100000", "250k", "500k", "million", "millionär")) return Intent.TargetDate;
        if (Has(q, "leisten", "afford", "kann ich mir")) return Intent.Affordability;
        if (Has(q, "bereut", "regret", "schlecht", "unnötig", "unnoetig")) return Intent.RegrettedSpending;
        if (Has(q, "worth it", "gelohnt", "gut ausgegeben", "war es wert", "was war gut")) return Intent.WorthwhileSpending;
        if (Has(q, "reduzieren", "einsparen", "sparen", "weglassen", "cut back", "reduce")) return Intent.WhatToReduce;
        if (Has(q, "warum", "verändert", "veraendert", "changed", "weniger als", "mehr als")) return Intent.WhatChanged;
        if (Has(q, "wo ist", "wohin", "money go", "wo ging", "ausgegeben")) return Intent.WhereMoneyWent;
        if (Has(q, "zusammenfassung", "monatsübersicht", "monatsuebersicht", "monthly summary", "diesen monat", "this month")) return Intent.MonthlySummary;
        return Intent.General;
    }

    private static CoachAnswer WhereMoneyWent(CoachContext c, bool de)
    {
        var top = c.Categories.Take(3).ToList();
        var merchants = c.Merchants.Take(3).ToList();
        var changes = c.Categories.OrderByDescending(x => Math.Abs(x.Delta)).Take(2).ToList();
        var categoryText = top.Count == 0 ? "—" : string.Join(", ", top.Select(x => $"{x.Name}: {CoachContextBuilder.FormatMoney(x.Amount, c.Currency)}"));
        var merchantText = merchants.Count == 0 ? "—" : string.Join(", ", merchants.Select(x => $"{x.Name}: {CoachContextBuilder.FormatMoney(x.Amount, c.Currency)}"));
        var changeText = changes.Count == 0 ? "—" : string.Join(", ", changes.Select(x => $"{x.Name} {(x.Delta >= 0 ? "+" : "")}{CoachContextBuilder.FormatMoney(x.Delta, c.Currency)}"));
        var nuance = ReviewNuance(c, de);
        var text = de
            ? $"Du hast {CoachContextBuilder.FormatMoney(c.Outgoing, c.Currency)} ausgegeben. Größte Bereiche: {categoryText}. Größte Empfänger: {merchantText}. Auffällige Veränderungen: {changeText}.{nuance}{DataWarning(c, de)}"
            : $"You spent {CoachContextBuilder.FormatMoney(c.Outgoing, c.Currency)}. Largest categories: {categoryText}. Largest payees: {merchantText}. Notable changes: {changeText}.{nuance}{DataWarning(c, de)}";
        var facts = top.Select(CategoryFactId)
            .Concat(merchants.Select(MerchantFactId))
            .Concat(changes.Select(CategoryFactId));
        return Result(text, c, facts,
            de ? ["Was hat sich verändert?", "Was habe ich bereut?", "Was könnte ich reduzieren?"] : ["What changed?", "What did I regret?", "What could I reduce?"]);
    }

    private static CoachAnswer WhatChanged(CoachContext c, bool de)
    {
        var delta = c.Outgoing - c.PreviousOutgoing;
        var changed = c.Categories.OrderByDescending(x => Math.Abs(x.Delta)).Take(3).ToList();
        var details = changed.Count == 0 ? "—" : string.Join(", ", changed.Select(x => $"{x.Name} {(x.Delta >= 0 ? "+" : "")}{CoachContextBuilder.FormatMoney(x.Delta, c.Currency)}"));
        var text = de
            ? $"Die Ausgaben haben sich um {(delta >= 0 ? "+" : "")}{CoachContextBuilder.FormatMoney(delta, c.Currency)} verändert. Größte Veränderungen: {details}.{DataWarning(c, de)}"
            : $"Spending changed by {(delta >= 0 ? "+" : "")}{CoachContextBuilder.FormatMoney(delta, c.Currency)}. Largest changes: {details}.{DataWarning(c, de)}";
        return Result(text, c, changed.Select(CategoryFactId),
            de ? ["Wo könnte ich reduzieren?", "Was war trotzdem gut ausgegeben?"] : ["Where could I cut back?", "What was still worth it?"]);
    }

    private static CoachAnswer WhatToReduce(CoachContext c, bool de)
    {
        var candidates = c.Categories
            .Where(x => x.NegativeReviewedAmount > 0m || x.Delta > 0m || x.BudgetOverage > 0m)
            .Select(x =>
            {
                var reviewedAmount = x.Amount * x.ReviewCoverage;
                var negativeShare = reviewedAmount > 0m ? Math.Min(1m, x.NegativeReviewedAmount / reviewedAmount) : 0m;
                var reviewConfidence = .5m + Math.Min(1m, x.ReviewCoverage);
                var negativeSignal = x.NegativeReviewedAmount * (1m + negativeShare) * reviewConfidence;
                var score = negativeSignal
                            + x.AvoidableNegativeReviewedAmount * .75m
                            + Math.Max(0m, x.Delta) * .35m
                            + x.BudgetOverage * 1.15m
                            - x.PositiveReviewedAmount * .45m;
                return new { Category = x, Score = score };
            })
            .OrderByDescending(x => x.Score).Take(3).ToList();
        if (candidates.Count == 0)
        {
            var fallback = c.Categories.OrderByDescending(x => x.Delta).Take(2).ToList();
            var text = de
                ? "Es gibt noch kein starkes negatives Review- oder Budgetsignal. Nach Ausgabenveränderungen wären zuerst prüfenswert: " + string.Join(", ", fallback.Select(x => x.Name)) + ". Bewerte mehr Ausgaben, damit die Empfehlung persönlicher wird." + DataWarning(c, de)
                : "There is no strong negative review or budget signal yet. Based on spending changes, inspect: " + string.Join(", ", fallback.Select(x => x.Name)) + ". Review more spending to make the recommendation more personal." + DataWarning(c, de);
            return Result(text, c, fallback.Select(CategoryFactId), []);
        }
        var details = string.Join(", ", candidates.Select(x => ReductionReason(x.Category, c.Currency, de)));
        var positive = c.Reviews.HighSpendPositive.FirstOrDefault();
        var nuance = positive is null ? "" : de
            ? $" {positive.Label} ist hoch, wurde aber überwiegend positiv bewertet und wird deshalb nicht allein wegen der Höhe priorisiert."
            : $" {positive.Label} is high but mostly rated positively, so it is not prioritized just because of its size.";
        var textWithWarning = de
            ? $"Prüfen würde ich zuerst: {details}.{nuance}{DataWarning(c, de)}"
            : $"I would inspect these first: {details}.{nuance}{DataWarning(c, de)}";
        var factIds = candidates.Select(x => CategoryFactId(x.Category)).ToList();
        foreach (var candidate in candidates.Where(x => x.Category.BudgetOverage > 0m && x.Category.CategoryId.HasValue))
            factIds.AddRange(c.Budgets.Where(b => b.CategoryId == candidate.Category.CategoryId && b.Overage > 0m).Select(b => $"budget:{b.BudgetId}:status"));
        return Result(textWithWarning, c, factIds,
            de ? ["Was habe ich bereut?", "Was war es wert?"] : ["What did I regret?", "What was worth it?"]);
    }

    private static string ReductionReason(CoachCategoryFact category, string currency, bool de)
    {
        var reasons = new List<string>();
        if (category.NegativeReviewedAmount > 0m)
            reasons.Add(de ? $"{CoachContextBuilder.FormatMoney(category.NegativeReviewedAmount, currency)} negativ bewertet" : $"{CoachContextBuilder.FormatMoney(category.NegativeReviewedAmount, currency)} rated negative");
        if (category.AvoidableNegativeReviewedAmount > 0m)
            reasons.Add(de ? "mit vermeidbaren Gründen" : "with avoidable reasons");
        if (category.BudgetOverage > 0m)
            reasons.Add(de ? $"Budget {CoachContextBuilder.FormatMoney(category.BudgetOverage, currency)} überzogen" : $"budget over by {CoachContextBuilder.FormatMoney(category.BudgetOverage, currency)}");
        if (category.Delta > 0m)
            reasons.Add(de ? $"+{CoachContextBuilder.FormatMoney(category.Delta, currency)} zum Vergleichszeitraum" : $"+{CoachContextBuilder.FormatMoney(category.Delta, currency)} vs comparison period");
        return $"{category.Name} ({string.Join(", ", reasons)})";
    }

    private static CoachAnswer Regretted(CoachContext c, bool de)
    {
        if (c.NegativeExamples.Count > 0)
        {
            var list = string.Join(", ", c.NegativeExamples.Take(5).Select(x => $"{x.Label}: {CoachContextBuilder.FormatMoney(x.Amount, c.Currency)}"));
            return Result((de ? $"Von dir konkret negativ bewertet: {list}. Das ist dein eigenes Review-Signal, keine Ableitung aus der Höhe." : $"You explicitly rated these negatively: {list}. This is your own review signal, not an inference from spending size.") + DataWarning(c, de), c,
                c.NegativeExamples.Take(5).Select(x => $"review:{x.TransactionId}"),
                de ? ["Was könnte ich reduzieren?", "Was war es wert?"] : ["What could I reduce?", "What was worth it?"]);
        }
        var groups = c.Reviews.NegativeOpportunities.Take(5).ToList();
        if (groups.Count == 0) return Result((de ? "Noch keine ausreichend aussagekräftigen negativen Bewertungen." : "There are not enough meaningful negative reviews yet.") + DataWarning(c, de), c, [], []);
        var grouped = string.Join(", ", groups.Select(x => $"{x.Label}: {CoachContextBuilder.FormatMoney(x.NegativeAmount, c.Currency)}"));
        return Result((de ? $"Stärkste Bereu-Signale: {grouped}. Das basiert auf deinen Bewertungen, nicht nur auf der Höhe." : $"Strongest regret signals: {grouped}. This is based on your reviews, not spending size alone.") + DataWarning(c, de), c, [],
            de ? ["Was könnte ich reduzieren?", "Was war es wert?"] : ["What could I reduce?", "What was worth it?"]);
    }

    private static CoachAnswer Worthwhile(CoachContext c, bool de)
    {
        if (c.PositiveExamples.Count > 0)
        {
            var list = string.Join(", ", c.PositiveExamples.Take(5).Select(x => $"{x.Label}: {CoachContextBuilder.FormatMoney(x.Amount, c.Currency)}"));
            return Result((de ? $"Von dir konkret positiv bewertet: {list}." : $"You explicitly rated these positively: {list}.") + DataWarning(c, de), c,
                c.PositiveExamples.Take(5).Select(x => $"review:{x.TransactionId}"),
                de ? ["Was habe ich bereut?", "Wo ist mein Geld hin?"] : ["What did I regret?", "Where did my money go?"]);
        }
        var groups = c.Reviews.HighSpendPositive.Take(5).ToList();
        if (groups.Count == 0) return Result((de ? "Noch keine ausreichend abgedeckten, klar positiv bewerteten Ausgabengruppen." : "There are not enough clearly positive reviewed groups yet.") + DataWarning(c, de), c, [], []);
        var grouped = string.Join(", ", groups.Select(x => $"{x.Label}: {CoachContextBuilder.FormatMoney(x.PositiveAmount, c.Currency)}"));
        return Result((de ? $"Nach deinen Bewertungen waren diese Ausgaben besonders eher ihr Geld wert: {grouped}." : $"Based on your reviews, these were especially likely to be worth it: {grouped}.") + DataWarning(c, de), c, [],
            de ? ["Was habe ich bereut?", "Wo ist mein Geld hin?"] : ["What did I regret?", "Where did my money go?"]);
    }

    private CoachAnswer TargetDate(string question, CoachContext c, bool de)
    {
        if (!c.CurrentNetWorth.HasValue) return Result(de ? "Für eine Zielprojektion fehlt ein aktueller Nettovermögenswert." : "A current net-worth value is missing.", c, [], []);
        if (!c.AverageMonthlySavings.HasValue)
            return Result(de ? "Für eine Zielprojektion fehlen vollständige Daten für den 90-Tage-Überschuss, zum Beispiel wegen eines fehlenden historischen Wechselkurses." : "A target projection needs complete 90-day surplus data; for example, a historical FX rate may be missing.", c, ["networth:current"], []);
        if (c.AverageMonthlySavings.Value <= 0m) return Result(de ? "Der 90-Tage-Durchschnitt zeigt keinen positiven monatlichen Überschuss; deshalb wäre ein Zieldatum erfunden." : "The 90-day average shows no positive monthly surplus, so a target date would be invented.", c, ["networth:current", "savings:monthly-average"], []);

        var annualReturn = ParseAnnualReturn(question);
        var explicitTarget = ParseMoney(question);
        IReadOnlyList<CoachTargetScenario> scenarios = explicitTarget is >= 10_000m
            ? [ProjectTarget(explicitTarget.Value, c.CurrentNetWorth, c.AverageMonthlySavings, annualReturn)]
            : BuildTargetScenarios(c, annualReturn);
        var lines = scenarios.Select(x => x.EstimatedDate.HasValue
            ? $"{CoachContextBuilder.FormatMoney(x.Target, c.Currency)} ≈ {x.EstimatedDate:yyyy-MM}"
            : $"{CoachContextBuilder.FormatMoney(x.Target, c.Currency)}: —");
        var assumption = annualReturn.HasValue
            ? (de ? $" mit angenommener Rendite von {annualReturn.Value:P1}" : $" with an assumed return of {annualReturn.Value:P1}")
            : (de ? " ohne angenommene Rendite" : " with no assumed return");
        var text = de
            ? $"„Reich“ hat keine feste Grenze. Projektion{assumption}: {string.Join("; ", lines)}. Das sind Szenarien, keine Zusagen.{DataWarning(c, de)}"
            : $"There is no single definition of rich. Projection{assumption}: {string.Join("; ", lines)}. These are scenarios, not guarantees.{DataWarning(c, de)}";
        return Result(text, c, ["networth:current", "savings:monthly-average"], de ? ["Wo könnte ich mehr sparen?"] : ["Where could I save more?"]);
    }

    private static CoachAnswer FinancialIndependence(string question, CoachContext c, bool de)
    {
        var explicitTarget = ParseMoney(question);
        if (explicitTarget is >= 10_000m)
        {
            if (!c.CurrentNetWorth.HasValue || !c.AverageMonthlySavings.HasValue || c.AverageMonthlySavings <= 0m)
                return Result(de ? "Für dieses Ziel fehlen ein belastbarer Nettovermögenswert oder ein positiver monatlicher Überschuss; deshalb nenne ich kein erfundenes Datum." : "A reliable net worth value or positive monthly surplus is missing for this target, so I will not invent a date.", c, ["networth:current", "savings:monthly-average"], []);
            var scenario = ProjectTarget(explicitTarget.Value, c.CurrentNetWorth, c.AverageMonthlySavings, ParseAnnualReturn(question));
            var date = scenario.EstimatedDate.HasValue ? scenario.EstimatedDate.Value.ToString("yyyy-MM", CultureInfo.InvariantCulture) : "—";
            return Result(de
                    ? $"Wenn du finanzielle Unabhängigkeit für dich mit {CoachContextBuilder.FormatMoney(explicitTarget.Value, c.Currency)} Zielvermögen definierst, ergibt die deterministische Projektion ungefähr {date}. Das ist ein Szenario, keine Zusage."
                    : $"If you define financial independence as {CoachContextBuilder.FormatMoney(explicitTarget.Value, c.Currency)} of target wealth, the deterministic projection is approximately {date}. This is a scenario, not a guarantee.",
                c, ["networth:current", "savings:monthly-average"], []);
        }

        var periodDays = Math.Max(1, c.To.DayNumber - c.From.DayNumber + 1);
        var annualizedSpending = c.Outgoing / periodDays * 365m;
        var text = de
            ? $"Für finanzielle Unabhängigkeit brauche ich eine explizite Zieldefinition, statt still eine Entnahmerate zu erfinden. Deine Ausgaben im betrachteten Zeitraum entsprechen hochgerechnet etwa {CoachContextBuilder.FormatMoney(annualizedSpending, c.Currency)} pro Jahr. Nenne mir ein Zielvermögen oder eine gewünschte Entnahme-Annahme, dann kann ich das Datum deterministisch berechnen.{DataWarning(c, de)}"
            : $"Financial independence needs an explicit target definition; I will not silently invent a withdrawal rate. Spending in the selected period annualizes to about {CoachContextBuilder.FormatMoney(annualizedSpending, c.Currency)} per year. Give me a target wealth amount or withdrawal assumption and I can calculate a deterministic date.{DataWarning(c, de)}";
        return Result(text, c, ["cashflow:outgoing", "networth:current", "savings:monthly-average"],
            de ? ["Wann erreiche ich 500.000 €?", "Wo könnte ich mehr sparen?"] : ["When could I reach €500,000?", "Where could I save more?"]);
    }

    private static CoachAnswer Affordability(string question, CoachContext c, bool de)
    {
        var amount = ParseMoney(question);
        if (!amount.HasValue) return Result(de ? "Nenne einen Betrag, damit ich die Leistbarkeit anhand von Liquidität, Cashflow und Budgets prüfen kann." : "Give me an amount so I can check affordability against liquidity, cash flow and budgets.", c, [], []);
        if (!c.AverageMonthlySavings.HasValue)
            return Result(de ? "Für eine belastbare Leistbarkeitsprüfung fehlen vollständige 90-Tage-Cashflow-Daten, zum Beispiel wegen eines fehlenden historischen Wechselkurses." : "A reliable affordability check needs complete 90-day cash-flow data; for example, a historical FX rate may be missing.", c, [], []);

        var monthly = c.AverageMonthlySavings.Value;
        var months = monthly > 0m ? amount.Value / monthly : (decimal?)null;
        var liquidText = c.LiquidAccountBalance.HasValue
            ? (de ? $" Sichtbare Kontoliquidität: {CoachContextBuilder.FormatMoney(c.LiquidAccountBalance.Value, c.Currency)}." : $" Visible account liquidity: {CoachContextBuilder.FormatMoney(c.LiquidAccountBalance.Value, c.Currency)}.")
            : (de ? " Ein vollständiger Liquiditätswert fehlt." : " A complete liquidity value is missing.");
        var matchingBudgets = c.Budgets.Where(x => string.Equals(x.Currency, c.Currency, StringComparison.OrdinalIgnoreCase)).ToList();
        var tightest = matchingBudgets.OrderBy(x => x.Remaining).FirstOrDefault();
        var budgetText = tightest is null
            ? (de ? " Es gibt keinen passenden aktiven Budgetstatus für diese Einschätzung." : " There is no matching active budget status for this check.")
            : tightest.Remaining < 0m
                ? (de ? $" Engstes Budget: {tightest.Name}, bereits {CoachContextBuilder.FormatMoney(-tightest.Remaining, c.Currency)} überzogen." : $" Tightest budget: {tightest.Name}, already over by {CoachContextBuilder.FormatMoney(-tightest.Remaining, c.Currency)}.")
                : (de ? $" Engstes Budget: {tightest.Name}, noch {CoachContextBuilder.FormatMoney(tightest.Remaining, c.Currency)} frei." : $" Tightest budget: {tightest.Name}, {CoachContextBuilder.FormatMoney(tightest.Remaining, c.Currency)} remaining.");
        var surplusText = months.HasValue
            ? (de ? $" Der Betrag entspricht etwa {months.Value:0.0} Monaten deines aktuellen Durchschnittsüberschusses." : $" The amount equals about {months.Value:0.0} months of your current average surplus.")
            : (de ? " Der 90-Tage-Durchschnitt zeigt keinen positiven monatlichen Überschuss." : " The 90-day average shows no positive monthly surplus.");
        var reserveWarning = de
            ? " Eine Notfallreserve wird nur berücksichtigt, wenn sie explizit als Daten vorliegt; deshalb ist das keine Aussage, dass der Kauf sicher ist."
            : " An emergency reserve is only included when explicitly available as data, so this is not a claim that the purchase is safe.";
        var text = $"{CoachContextBuilder.FormatMoney(amount.Value, c.Currency)}.{liquidText}{surplusText}{budgetText}{reserveWarning}{DataWarning(c, de)}";
        var factIds = new List<string> { "savings:monthly-average", "cashflow:net" };
        if (c.LiquidAccountBalance.HasValue) factIds.Add("wealth:liquid-accounts");
        if (c.TotalDebt.HasValue) factIds.Add("wealth:debt");
        if (tightest is not null) factIds.Add($"budget:{tightest.BudgetId}:status");
        return Result(text, c, factIds, de ? ["Wo könnte ich dafür sparen?"] : ["Where could I save for it?"]);
    }

    private static CoachAnswer MonthlySummary(CoachContext c, bool de)
    {
        var score = c.Reviews.WorthItScore?.ToString("0.00", CultureInfo.InvariantCulture) ?? "—";
        var budgetOver = c.Budgets.Count(x => x.Overage > 0m);
        var debt = c.TotalDebt.HasValue ? CoachContextBuilder.FormatMoney(c.TotalDebt.Value, c.Currency) : "—";
        var text = de
            ? $"Einnahmen: {CoachContextBuilder.FormatMoney(c.Income, c.Currency)}, Ausgaben: {CoachContextBuilder.FormatMoney(c.Outgoing, c.Currency)}, Netto-Cashflow: {CoachContextBuilder.FormatMoney(c.NetCashFlow, c.Currency)}. Review-Abdeckung: {c.Reviews.ReviewCoverage:P0}, Worth-it-Score: {score}. Überzogene Budgets: {budgetOver}, erfasste Verbindlichkeiten: {debt}.{DataWarning(c, de)}"
            : $"Income: {CoachContextBuilder.FormatMoney(c.Income, c.Currency)}, spending: {CoachContextBuilder.FormatMoney(c.Outgoing, c.Currency)}, net cash flow: {CoachContextBuilder.FormatMoney(c.NetCashFlow, c.Currency)}. Review coverage: {c.Reviews.ReviewCoverage:P0}, worth-it score: {score}. Budgets over target: {budgetOver}, recorded debt: {debt}.{DataWarning(c, de)}";
        var facts = new List<string> { "cashflow:income", "cashflow:outgoing", "cashflow:net", "reviews:coverage" };
        if (c.TotalDebt.HasValue) facts.Add("wealth:debt");
        facts.AddRange(c.Budgets.Where(x => x.Overage > 0m).Take(3).Select(x => $"budget:{x.BudgetId}:status"));
        return Result(text, c, facts,
            de ? ["Wo ist mein Geld hin?", "Was habe ich bereut?", "Was war es wert?"] : ["Where did my money go?", "What did I regret?", "What was worth it?"]);
    }

    private static CoachAnswer General(CoachContext c, bool de)
    {
        var text = de
            ? $"Ich kann deine FullWorth-Daten deterministisch auswerten. Für den gewählten Zeitraum sehe ich {CoachContextBuilder.FormatMoney(c.Income, c.Currency)} Einnahmen, {CoachContextBuilder.FormatMoney(c.Outgoing, c.Currency)} Ausgaben und {CoachContextBuilder.FormatMoney(c.NetCashFlow, c.Currency)} Netto-Cashflow. Frag zum Beispiel nach Veränderungen, bereuten Ausgaben, Leistbarkeit oder einem Vermögensziel.{DataWarning(c, de)}"
            : $"I can analyze your FullWorth data deterministically. For the selected period I see {CoachContextBuilder.FormatMoney(c.Income, c.Currency)} income, {CoachContextBuilder.FormatMoney(c.Outgoing, c.Currency)} spending and {CoachContextBuilder.FormatMoney(c.NetCashFlow, c.Currency)} net cash flow. Ask about changes, regretted spending, affordability or a wealth target.{DataWarning(c, de)}";
        return Result(text, c, ["cashflow:income", "cashflow:outgoing", "cashflow:net"],
            de ? ["Wo ist mein Geld hin?", "Was könnte ich reduzieren?", "Wann erreiche ich 100.000 €?"] : ["Where did my money go?", "What could I reduce?", "When could I reach €100,000?"]);
    }

    private static CoachAnswer Result(string text, CoachContext context, IEnumerable<string> factIds, IReadOnlyList<string> followUps)
    {
        var ids = factIds.ToHashSet(StringComparer.Ordinal);
        if (context.Incomplete) ids.Add("data:incomplete");
        return new(text, CoachAnswerMode.Deterministic, context.Facts.Where(x => ids.Contains(x.Id)).ToList(), followUps);
    }

    private static string ReviewNuance(CoachContext c, bool de)
    {
        var positive = c.Reviews.HighSpendPositive.FirstOrDefault();
        var negative = c.Reviews.NegativeOpportunities.FirstOrDefault();
        if (positive is null && negative is null) return "";
        var parts = new List<string>();
        if (positive is not null) parts.Add(de ? $" {positive.Label} wurde überwiegend positiv bewertet" : $" {positive.Label} was rated mostly positively");
        if (negative is not null) parts.Add(de ? $" {negative.Label} hat ein stärkeres negatives Review-Signal" : $" {negative.Label} has a stronger negative review signal");
        return string.Join(";", parts) + ".";
    }

    private static string DataWarning(CoachContext c, bool de) => !c.Incomplete
        ? ""
        : de
            ? " Einige Werte sind unvollständig, weil Finanzkomponenten oder mindestens ein historischer Wechselkurs fehlen."
            : " Some values are incomplete because financial components or at least one historical FX rate are missing.";

    private static decimal? ParseMoney(string question)
    {
        var kMatch = Regex.Match(question, @"(?<!\w)(\d+(?:[.,]\d+)?)\s*[kK](?!\w)");
        if (kMatch.Success && decimal.TryParse(kMatch.Groups[1].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var kValue) && kValue > 0m)
            return kValue * 1_000m;
        var matches = Regex.Matches(question, @"(?<!\w)(\d{1,3}(?:[. ]\d{3})*(?:,\d{1,2})?|\d+(?:[.,]\d{1,2})?)(?!\w|\s*%)");
        if (matches.Count == 0) return null;
        var raw = matches[^1].Groups[1].Value.Replace(" ", "");
        if (raw.Contains(',') && raw.Contains('.')) raw = raw.Replace(".", "").Replace(',', '.');
        else if (raw.Contains(',')) raw = raw.Replace(',', '.');
        else if (raw.Count(c => c == '.') > 1 || (raw.Contains('.') && raw.Split('.')[^1].Length == 3)) raw = raw.Replace(".", "");
        return decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) && value > 0m ? value : null;
    }

    private static decimal? ParseAnnualReturn(string question)
    {
        var match = Regex.Match(question, @"(\d+(?:[.,]\d+)?)\s*%");
        if (!match.Success) return null;
        if (!decimal.TryParse(match.Groups[1].Value.Replace(',', '.'), NumberStyles.Number, CultureInfo.InvariantCulture, out var percent)) return null;
        return percent is > 0m and <= 30m ? percent / 100m : null;
    }

    private static string CategoryFactId(CoachCategoryFact x) => $"category:{x.CategoryId?.ToString() ?? "uncategorized"}:current";
    private static string MerchantFactId(CoachMerchantFact x) => $"merchant:{CoachContextBuilder.NormalizeMerchantKey(x.Name)}:current";
    private static bool Has(string value, params string[] needles) => needles.Any(value.Contains);
    private static bool IsGerman(string question)
    {
        var q = " " + question.ToLowerInvariant() + " ";
        return Has(q, " ich ", "mein", "geld", "ausgabe", "warum", "reich", "leisten", "sparen", "monat", "unabhängig", " wo ", " was ") || !Has(q, "what", "where", "why", "afford", "spend", "money", "month", "financially");
    }
}
