namespace Susurri.Modules.IAM.Core.Exceptions;

public class InvalidHexStringLengthException : CustomException
{
    public InvalidHexStringLengthException() : base($"Hex string length must be even.")
    {
    }
}