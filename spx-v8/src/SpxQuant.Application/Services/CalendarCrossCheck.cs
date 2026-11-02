using SpxQuant.Application.Abstractions;
using SpxQuant.Domain.Calendar;

namespace SpxQuant.Application.Services;

public sealed record CalendarDiscrepancy(DateOnly Date, string Local, string Broker);

/// <summary>Compares the local calendar file with the broker's calendar. Any discrepancy must be resolved by a person.</summary>
public sealed class CalendarCrossCheck(ITradingCalendarProvider calendar, IBrokerSessionInfo broker)
{
    public async Task<IReadOnlyList<CalendarDiscrepancy>> CheckMonthAsync(int year, int month, CancellationToken ct)
    {
        var brokerDays = await broker.GetCalendarMonthAsync(year, month, ct);
        var result = new List<CalendarDiscrepancy>();
        foreach (var b in brokerDays)
        {
            var local = calendar.Calendar.SessionFor(b.Date);
            var localDesc = Describe(local.Status, local.Close is { } c ? TimeOnly.FromDateTime(Domain.Common.EasternTime.ToEastern(c).DateTime) : null);
            var brokerDesc = Describe(b.Status, b.Status == SessionStatus.Closed ? null : b.Close ?? calendar.Calendar.Rules.RegularClose);
            if (localDesc != brokerDesc) result.Add(new(b.Date, localDesc, brokerDesc));
        }
        return result;
    }

    private static string Describe(SessionStatus status, TimeOnly? close) => status switch
    {
        SessionStatus.Closed => "Closed",
        SessionStatus.Unknown => "Unknown",
        _ => $"Open until {close:HH\\:mm}",
    };
}
