namespace Susurri.Shared.Abstractions.Exceptions;

public abstract class SusurriException : Exception
{
    protected SusurriException(string message) : base(message)
    {
    }
}