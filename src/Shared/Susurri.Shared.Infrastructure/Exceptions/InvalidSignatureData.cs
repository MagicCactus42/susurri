using Susurri.Shared.Abstractions.Exceptions;

namespace Susurri.Shared.Infrastructure.Exceptions;

public class InvalidSignatureData : SusurriException
{
    public InvalidSignatureData(object data) : base("Invalid signature data")
    {
    }
}