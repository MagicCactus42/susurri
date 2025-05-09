namespace Susurri.Modules.Users.Core.Exceptions;

public class InvalidPassphraseException : CustomException
{
    public InvalidPassphraseException() : base($"Passphrase must contain between 8 and 16 words.")
    {
    }
}