namespace FullWorth.Backend.Modules.Parity;

public readonly record struct TwrSubperiod(decimal StartValue, decimal ExternalFlowAtStart, decimal EndValue);
public readonly record struct DatedCashFlow(DateOnly Date, decimal Amount);

public static class InvestmentPerformanceMath
{
    /// <summary>
    /// Chains sub-period returns after breaking the series at every external cash flow.
    /// ExternalFlowAtStart is capital entering (+) or leaving (-) at the start of the subperiod.
    /// Returns null instead of inventing a percentage when a subperiod has no positive capital base.
    /// </summary>
    public static decimal? TimeWeightedReturn(IReadOnlyList<TwrSubperiod> periods)
    {
        if (periods.Count == 0) return null;
        decimal factor = 1m;
        foreach (var period in periods)
        {
            var capital = period.StartValue + period.ExternalFlowAtStart;
            if (capital <= 0m || period.EndValue < 0m) return null;
            factor *= period.EndValue / capital;
        }
        return factor - 1m;
    }

    /// <summary>
    /// Calculates a dated money-weighted return. The solver first scans for sign-changing brackets;
    /// multiple roots are treated as ambiguous and return null. One unique bracket is solved with
    /// bounded bisection. This deliberately favors "unavailable" over a misleading personal return.
    /// </summary>
    public static decimal? Xirr(IReadOnlyList<DatedCashFlow> cashFlows)
    {
        if (cashFlows.Count < 2) return null;
        var ordered = cashFlows.OrderBy(flow => flow.Date).ToArray();
        if (!ordered.Any(flow => flow.Amount < 0m) || !ordered.Any(flow => flow.Amount > 0m)) return null;
        if (ordered[0].Date == ordered[^1].Date) return null;

        var firstDate = ordered[0].Date;
        double Npv(double rate)
        {
            if (rate <= -1d) return double.NaN;
            var total = 0d;
            foreach (var flow in ordered)
            {
                var years = (flow.Date.DayNumber - firstDate.DayNumber) / 365.0d;
                total += (double)flow.Amount / Math.Pow(1d + rate, years);
            }
            return total;
        }

        // A logarithmic-ish grid covers large negative/positive returns without relying on Newton
        // convergence. Multiple sign changes mean multiple mathematical IRRs, which are not safe to
        // present as one definitive personal return.
        var grid = new[]
        {
            -0.9999d,-0.99d,-0.95d,-0.9d,-0.8d,-0.6d,-0.4d,-0.2d,-0.1d,0d,
            0.05d,0.1d,0.15d,0.2d,0.3d,0.5d,0.75d,1d,1.5d,2d,3d,5d,8d,12d,20d,50d,100d
        };
        var brackets = new List<(double A,double B)>();
        double? previousRate = null;
        double? previousValue = null;
        foreach (var rate in grid)
        {
            var value = Npv(rate);
            if (double.IsNaN(value) || double.IsInfinity(value)) continue;
            if (Math.Abs(value) < 1e-10)
            {
                // Exact grid root is still ambiguous if another root exists elsewhere. Add a tiny
                // bracket around it and let the uniqueness check below decide.
                brackets.Add((Math.Max(-0.999999d, rate-1e-7d), rate+1e-7d));
            }
            if (previousRate.HasValue && previousValue.HasValue && Math.Sign(previousValue.Value) != Math.Sign(value))
                brackets.Add((previousRate.Value, rate));
            previousRate = rate;
            previousValue = value;
        }

        // Collapse brackets that describe the same root (for example an exact grid hit plus its
        // adjacent sign-change bracket).
        var roots = new List<double>();
        foreach (var bracket in brackets)
        {
            var root = Bisect(Npv, bracket.A, bracket.B);
            if (!root.HasValue) continue;
            if (roots.All(existing => Math.Abs(existing-root.Value) > 1e-6d)) roots.Add(root.Value);
        }
        if (roots.Count != 1) return null;
        if (roots[0] <= -1d || double.IsNaN(roots[0]) || double.IsInfinity(roots[0])) return null;
        return decimal.Round((decimal)roots[0], 10, MidpointRounding.AwayFromZero);
    }

    private static double? Bisect(Func<double,double> function, double a, double b)
    {
        var fa = function(a); var fb = function(b);
        if (double.IsNaN(fa) || double.IsNaN(fb)) return null;
        if (Math.Abs(fa) < 1e-10) return a;
        if (Math.Abs(fb) < 1e-10) return b;
        if (Math.Sign(fa) == Math.Sign(fb))
        {
            var mid = (a+b)/2d;
            var fm = function(mid);
            return Math.Abs(fm) < 1e-8 ? mid : null;
        }
        for (var iteration=0; iteration<120; iteration++)
        {
            var mid = (a+b)/2d;
            var fm = function(mid);
            if (double.IsNaN(fm)) return null;
            if (Math.Abs(fm) < 1e-10 || Math.Abs(b-a) < 1e-10) return mid;
            if (Math.Sign(fa) == Math.Sign(fm)) { a=mid; fa=fm; }
            else { b=mid; fb=fm; }
        }
        return (a+b)/2d;
    }
}
