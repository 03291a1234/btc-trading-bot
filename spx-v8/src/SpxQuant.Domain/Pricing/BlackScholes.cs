using SpxQuant.Domain.Common;
using SpxQuant.Domain.Market;

namespace SpxQuant.Domain.Pricing;

/// <summary>
/// European Black-Scholes (SPX/SPXW are European, cash-settled). Used to ESTIMATE Greeks locally when vendor Greeks are
/// missing or stale. Outputs are model estimates, never observed values. No dividend yield term (q=0) in Phase 2: for
/// sub-day tenors its effect on delta is far below the 0.15 selection tolerance, but it is a known simplification.
/// </summary>
public static class BlackScholes
{
    public const double MinYears = 1.0 / (365.0 * 24 * 60); // one minute

    public static double Price(double spot, double strike, double years, double vol, double rate, OptionRight right)
    {
        var (d1, d2) = D(spot, strike, years, vol, rate);
        var df = Math.Exp(-rate * years);
        return right == OptionRight.Call
            ? spot * N(d1) - strike * df * N(d2)
            : strike * df * N(-d2) - spot * N(-d1);
    }

    public static OptionGreeks Greeks(double spot, double strike, double years, double vol, double rate, OptionRight right, DateTimeOffset asOf)
    {
        var (d1, d2) = D(spot, strike, years, vol, rate);
        var sqrtT = Math.Sqrt(years);
        var delta = right == OptionRight.Call ? N(d1) : N(d1) - 1;
        var gamma = Pdf(d1) / (spot * vol * sqrtT);
        var df = Math.Exp(-rate * years);
        var thetaYear = -spot * Pdf(d1) * vol / (2 * sqrtT) + (right == OptionRight.Call ? -rate * strike * df * N(d2) : rate * strike * df * N(-d2));
        var vega = spot * Pdf(d1) * sqrtT / 100.0;
        return new OptionGreeks(R(delta, 4), R(gamma, 6), R(thetaYear / 365.0, 4), R(vega, 4), R(vol, 4), asOf);
    }

    /// <summary>Implied volatility by bisection on [1%, 500%]. Null when the price is outside no-arbitrage bounds.</summary>
    public static double? ImpliedVol(double price, double spot, double strike, double years, double rate, OptionRight right)
    {
        if (price <= 0 || spot <= 0 || strike <= 0 || years <= 0) return null;
        var df = Math.Exp(-rate * years);
        var intrinsic = right == OptionRight.Call ? Math.Max(0, spot - strike * df) : Math.Max(0, strike * df - spot);
        var upper = right == OptionRight.Call ? spot : strike * df;
        if (price <= intrinsic || price >= upper) return null;
        double lo = 0.01, hi = 5.0;
        if (Price(spot, strike, years, hi, rate, right) < price) return null;
        for (var i = 0; i < 100; i++)
        {
            var mid = (lo + hi) / 2;
            if (Price(spot, strike, years, mid, rate, right) > price) hi = mid; else lo = mid;
            if (hi - lo < 1e-7) break;
        }
        return (lo + hi) / 2;
    }

    /// <summary>
    /// Returns the quote with locally estimated Greeks (IV implied from the bid/ask mid) and GreeksProvenance=Estimated,
    /// or null when inputs are insufficient. Time to expiry runs to the session close (PM settlement).
    /// </summary>
    public static OptionQuote? WithEstimatedGreeks(OptionQuote q, decimal spot, DateTimeOffset now, DateTimeOffset settlement, double rate)
    {
        if (q.Mid is not { } mid || mid <= 0 || q.Bid is not > 0 || spot <= 0) return null;
        var years = (settlement - now).TotalDays / 365.0;
        if (years < MinYears) return null;
        var quoteAt = q.BidAt is { } b && q.AskAt is { } a ? (b < a ? b : a) : (DateTimeOffset?)null;
        if (quoteAt is null) return null;
        var iv = ImpliedVol((double)mid, (double)spot, (double)q.Strike, years, rate, q.Right);
        if (iv is null) return null;
        return q with { Greeks = Greeks((double)spot, (double)q.Strike, years, iv.Value, rate, q.Right, quoteAt.Value), GreeksProvenance = Provenance.Estimated };
    }

    private static (double, double) D(double s, double k, double t, double v, double r)
    {
        if (s <= 0 || k <= 0 || t <= 0 || v <= 0) throw new ArgumentOutOfRangeException(nameof(s), "Black-Scholes inputs must be positive");
        var d1 = (Math.Log(s / k) + (r + 0.5 * v * v) * t) / (v * Math.Sqrt(t));
        return (d1, d1 - v * Math.Sqrt(t));
    }

    private static decimal R(double v, int digits) => (decimal)Math.Round(v, digits);
    private static double Pdf(double x) => Math.Exp(-0.5 * x * x) / Math.Sqrt(2 * Math.PI);

    /// <summary>Standard normal CDF via a Chebyshev erfc approximation (fractional error /// <summary>Standard normal CDF via erfc (Abramowitz-Stegun 7.1.26 is too coarse; use W. J. Cody style rational approx).</summary>lt; 1.2e-7).</summary>
    private static double N(double x) => 0.5 * Erfc(-x / Math.Sqrt(2));

    private static double Erfc(double x)
    {
        // Numerical Recipes erfc (Chebyshev), fractional error < 1.2e-7.
        var z = Math.Abs(x);
        var t = 1.0 / (1.0 + 0.5 * z);
        var r = t * Math.Exp(-z * z - 1.26551223 + t * (1.00002368 + t * (0.37409196 + t * (0.09678418 + t * (-0.18628806 +
            t * (0.27886807 + t * (-1.13520398 + t * (1.48851587 + t * (-0.82215223 + t * 0.17087277)))))))));
        return x >= 0 ? r : 2.0 - r;
    }
}
