using Susurri.Shared.Abstractions.Commands;

namespace Susurri.Modules.IAM.Application.Commands;

public record Login(string Username, string Passphrase) : ICommand;