namespace SpxQuant.Infrastructure.Tradier;

public sealed class TradierOptions
{
    public const string Section = "Tradier";
    public string BaseUrl { get; set; } = "https://api.tradier.com";
    /// <summary>Supply via environment (Tradier__Token) or a secret store. Never commit.</summary>
    public string? Token { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    /// <summary>
    /// Time zone of greeks.updated_at ("yyyy-MM-dd HH:mm:ss", no offset). OBSERVED 2026-11-02 09:36 ET: every SPXW contract carried
    /// "2026-10-30 20:00:06", which is the prior session's 16:00:06 ET close only when read as UTC. Tradier Greeks are therefore an
    /// end-of-day snapshot and are stale all session; use Greeks:Source=LocalEstimated.
    /// </summary>
    public string GreeksTimestampZone { get; set; } = "UTC";
}
