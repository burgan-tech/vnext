using BBT.Aether.Clock;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BBT.Workflow.Data.ValueConverters;

internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    dt => dt,
    dt => Clock.NormalizeToUtc(dt))
{
    internal static readonly IClock Clock = new SystemClock();
}
