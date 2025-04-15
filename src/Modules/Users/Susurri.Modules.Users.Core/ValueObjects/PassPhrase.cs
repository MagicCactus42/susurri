using dotnetstandard_bip39;
using Susurri.Modules.Users.Core.Exceptions;
using Susurri.Shared.Abstractions.Security;

namespace Susurri.Modules.Users.Core.ValueObjects;

public sealed record PassPhrase
{
    public static readonly BIP39 Bip39 = new();
    public string Value { get; }

    public PassPhrase(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new NullPassphraseException(value);

        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (words.Length < SecurityLimits.MinPassphraseWords)
            throw new InvalidPassphraseException(
                $"Passphrase must contain at least {SecurityLimits.MinPassphraseWords} words");

        if (words.Length > SecurityLimits.MaxPassphraseWords)
            throw new InvalidPassphraseException(
                $"Passphrase cannot exceed {SecurityLimits.MaxPassphraseWords} words");

        Value = value;
    }

    public static implicit operator string(PassPhrase passPhrase) => passPhrase.Value;
    public static implicit operator PassPhrase(string passPhrase) => new(passPhrase);
    public override string ToString() => Value;
}