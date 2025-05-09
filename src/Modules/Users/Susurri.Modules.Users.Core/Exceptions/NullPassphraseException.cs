namespace Susurri.Modules.Users.Core.Exceptions;

public class NullPassphraseException : CustomException
{
    public NullPassphraseException(string passphrase) : base($"Passphrase: {passphrase} is invalid")
    {
    }
}