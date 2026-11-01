using System.Text.Json;
using System.Text.Json.Serialization;
using SpxQuant.Application.Abstractions;
using SpxQuant.Domain.Calendar;

namespace SpxQuant.Infrastructure.Calendar;

public sealed record CalendarFile(int SchemaVersion, DateOnly CoverageStart, DateOnly CoverageEnd, string Source, List<CalendarDay> Exceptions);

public sealed class StaticCalendarProvider(TradingCalendar calendar) : ITradingCalendarProvider
{
    public TradingCalendar Calendar => calendar;
}

public static class JsonCalendarLoader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public static TradingCalendar Load(string path)
    {
        var file = JsonSerializer.Deserialize<CalendarFile>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Empty calendar file");
        if (file.SchemaVersion != 1) throw new InvalidDataException($"Unsupported calendar schema {file.SchemaVersion}");
        return new TradingCalendar(file.CoverageStart, file.CoverageEnd, file.Exceptions ?? [], file.Source);
    }
}
