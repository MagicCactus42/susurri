namespace Susurri.Shared.Abstractions.Health;

public interface IAttestationProvider
{
    string? AttestationJson { get; }
}
