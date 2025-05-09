using dotnetstandard_bip39;
using Susurri.Modules.Users.Core.Exceptions;

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

        if (words.Length < 8 || words.Length > 16)
            throw new InvalidPassphraseException();

        Value = value;
    }
    
    public static implicit operator string(PassPhrase passPhrase) => passPhrase.Value;
    public static implicit operator PassPhrase(string passPhrase) => new(passPhrase);
    public override string ToString() => Value;
}