namespace SpxQuant.Domain.Common;

public static class EasternTime
{
    // IANA id; available on Linux/macOS and on Windows with ICU (.NET 6+).
    public static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

    public static DateTimeOffset ToEastern(DateTimeOffset instant) => TimeZoneInfo.ConvertTime(instant, Zone);

    public static DateOnly DateOf(DateTimeOffset instant) => DateOnly.FromDateTime(ToEastern(instant).DateTime);

    public static DateTimeOffset At(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, Zone.GetUtcOffset(local));
    }
}
