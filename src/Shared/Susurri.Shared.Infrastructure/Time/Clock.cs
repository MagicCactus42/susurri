using Susurri.Shared.Abstractions.Time;

namespace Susurri.Shared.Infrastructure.Time;

internal class Clock : IClock
{
    public DateTimeOffset CurrentTime() => DateTimeOffset.UtcNow;
}