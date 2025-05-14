namespace Susurri.Shared.Abstractions.Time;

public interface IClock
{
    DateTimeOffset CurrentTime();
}