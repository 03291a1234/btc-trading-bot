namespace SpxQuant.Domain.Common;

/// <summary>
/// Every number shown to a user or stored for research carries where it came from.
/// Acceptance criterion 10: observed, estimated and simulated results must never be conflated.
/// </summary>
public enum Provenance
{
    /// <summary>Received from a market-data vendor (e.g. a Tradier quote). Not independently verified.</summary>
    Observed,
    /// <summary>Derived by a model or assumption (fees, slippage, planned stop loss).</summary>
    Estimated,
    /// <summary>Produced by the paper/replay simulator. Not a brokerage fill.</summary>
    Simulated,
    /// <summary>Synthetic fixture data for tests and demos. Never market data.</summary>
    Synthetic,
}

public readonly record struct Labeled<T>(T Value, Provenance Provenance, string? Note = null);
