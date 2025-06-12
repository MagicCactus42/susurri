using Susurri.Shared.Abstractions.Exceptions;

namespace Susurri.Modules.Users.Core.Exceptions;

public class InvalidPassphraseException : SusurriException
{
    public InvalidPassphraseException() : base($"Passphrase must contain between 8 and 16 words.")
    {
    }
}