namespace SpxQuant.Infrastructure.Tradier;

public sealed class BrokerageAccessBlockedException(string message) : InvalidOperationException(message);

/// <summary>
/// Outbound safety interlock on the Tradier HttpClient. Only GET requests to /v1/markets/* may leave the process.
/// Orders (/v1/accounts/*/orders), account, position, balance, user and watchlist routes are blocked
/// regardless of what any caller tries. This is defense in depth on top of there being no order code at all.
/// </summary>
public sealed class ReadOnlyMarketDataGuard : DelegatingHandler
{
    public static readonly string[] AllowedPathPrefixes = ["/v1/markets/"];

    public static bool IsAllowed(HttpRequestMessage request) =>
        request.Method == HttpMethod.Get &&
        request.RequestUri is { IsAbsoluteUri: true } uri &&
        uri.Scheme == Uri.UriSchemeHttps &&
        AllowedPathPrefixes.Any(p => uri.AbsolutePath.StartsWith(p, StringComparison.Ordinal)) &&
        !uri.AbsolutePath.Contains("..", StringComparison.Ordinal);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!IsAllowed(request))
            throw new BrokerageAccessBlockedException($"Blocked non-market-data request: {request.Method} {request.RequestUri?.AbsolutePath}");
        return base.SendAsync(request, cancellationToken);
    }
}
