using Shouldly;
using Susurri.Modules.IAM.Application.Commands;
using Susurri.Modules.Users.Core.Abstractions;
using Susurri.Shared.Abstractions.Commands;

namespace Susurri.Tests.Unit;

public class Auth
{
    [Fact]
    public async Task Using_the_same_passphrase_should_result_in_creating_same_keys()
    {
        var command = new Login("testUser1", "harsh map mill could harsh man future win heart rapid woman race");
        await _commandDispatcher.SendAsync(command);

        var key1 = await _userRepository.GetKeyByUsernameAsync("testUser1");
        
        var command2 = new Login("testUser2", "harsh map mill could harsh man future win heart rapid woman race");
        await _commandDispatcher.SendAsync(command2);
        
        var key2 = await _userRepository.GetKeyByUsernameAsync("testUser2");
        
        key1.ShouldBe(key2);
    }


    #region arrange

    private readonly ICommandDispatcher _commandDispatcher;
    private readonly IUserRepository _userRepository;
    
    public Auth(ICommandDispatcher commandDispatcher, IUserRepository userRepository)
    {
        _commandDispatcher = commandDispatcher;
        _userRepository = userRepository;
    }
    
    #endregion
}