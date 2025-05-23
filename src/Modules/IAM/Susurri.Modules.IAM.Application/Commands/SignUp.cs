﻿using Susurri.Shared.Abstractions.Commands;

namespace Susurri.Modules.IAM.Application.Commands;

public record SignUp(string Username, string Passphrase) : ICommand;