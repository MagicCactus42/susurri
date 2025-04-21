using Susurri.Shared.Abstractions.Exceptions;

namespace Susurri.Modules.Users.Core.Exceptions;

public class NullPassphraseException : SusurriException
{
    public NullPassphraseException(string passphrase) : base($"Passphrase: {passphrase} is invalid")
    {
    }
}