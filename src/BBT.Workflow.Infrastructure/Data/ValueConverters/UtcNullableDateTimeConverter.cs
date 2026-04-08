using BBT.Aether.Clock;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace BBT.Workflow.Data.ValueConverters;

internal sealed class UtcNullableDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    dt => dt,
    dt => dt.HasValue ? UtcDateTimeConverter.Clock.NormalizeToUtc(dt.Value) : null);
