namespace SpxQuant.Domain.Market;

public enum OptionRight { Call, Put }

public static class OptionRightParser
{
    public static bool TryParse(string? text, out OptionRight right)
    {
        switch (text?.Trim().ToUpperInvariant())
        {
            case "CALL": case "C": right = OptionRight.Call; return true;
            case "PUT": case "P": right = OptionRight.Put; return true;
            default: right = default; return false;
        }
    }
}
