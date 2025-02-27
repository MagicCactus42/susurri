using Susurri.Shared.Abstractions.Exceptions;

namespace Susurri.Modules.IAM.Core.Exceptions;

public class InvalidHexStringLengthException : SusurriException
{
    public InvalidHexStringLengthException() : base($"Hex string length must be even.")
    {
    }
}