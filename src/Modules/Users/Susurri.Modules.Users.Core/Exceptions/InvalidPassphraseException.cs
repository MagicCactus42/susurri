using Susurri.Shared.Abstractions.Exceptions;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.Modules.Users.Core.Exceptions;

public class InvalidPassphraseException : SusurriException
{
    public InvalidPassphraseException()
        : base($"Passphrase must contain between {SecurityLimits.MinPassphraseWords} and {SecurityLimits.MaxPassphraseWords} words.")
    {
    }

    public InvalidPassphraseException(string message) : base(message)
    {
    }
}