using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Susurri.Api.Commands;
using Susurri.Application.Abstractions;
using Susurri.Core.Abstractions;
using Susurri.Core.DTO;
using Susurri.Core.Exceptions;
using Susurri.Core.Queries;

namespace Susurri.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UserController : ControllerBase
{
   private readonly IQueryHandler<GetUsers, IEnumerable<UserDto>> _getUsersHandler;
    private readonly IQueryHandler<GetUser, UserDto> _getUserHandler;
    private readonly ICommandHandler<SignUp> _signUpHandler;
    private readonly ICommandHandler<SignIn> _signInHandler;
    private readonly ITokenStorage _tokenStorage;
    private readonly IAuthenticator _authenticator;

    public UserController(ICommandHandler<SignUp> signUpHandler,
        ICommandHandler<SignIn> signInHandler,
        IQueryHandler<GetUsers, IEnumerable<UserDto>> getUsersHandler,
        IQueryHandler<GetUser, UserDto> getUserHandler,
        ITokenStorage tokenStorage, IAuthenticator authenticator)
    {
        _signUpHandler = signUpHandler;
        _signInHandler = signInHandler;
        _getUsersHandler = getUsersHandler;
        _getUserHandler = getUserHandler;
        _tokenStorage = tokenStorage;
        _authenticator = authenticator;
    }
    
    
    [HttpGet("{userId:guid}")]
    [Authorize(Policy = "is-dev")]
    public async Task<ActionResult<UserDto>> Get(Guid userId)
    {
        var user = await _getUserHandler.HandleAsync(new GetUser {UserId = userId});
        if (user is null)
        {
            return NotFound();
        }

        return user;
    }
    
    
    [HttpGet("me")]
    public async Task<ActionResult<UserDto>> Get()
    {
        if (string.IsNullOrWhiteSpace(User.Identity?.Name))
        {
            return NotFound();
        }

        var userId = Guid.Parse(User.Identity?.Name);
        var user = await _getUserHandler.HandleAsync(new GetUser {UserId = userId});
        
        return user;
    }

    [HttpGet("token/{username}")]
    public async Task<ActionResult<JwtDto>> GetToken(string username)
    {
        if (string.IsNullOrWhiteSpace(User.Identity?.Name))
        {
            return NotFound();
        }
        
        var userId = Guid.Parse(User.Identity?.Name);
        var jwtToken = _authenticator.CreateToken(userId, username, "user");

        if (jwtToken is null)
            throw new EmptyJWTException();
        await Task.CompletedTask;
        return jwtToken;
    }

    [HttpGet]
    [Authorize(Policy = "is-dev")]
    public async Task<ActionResult<IEnumerable<UserDto>>> Get([FromQuery] GetUsers query)
        => Ok(await _getUsersHandler.HandleAsync(query));

    [HttpPost]
    public async Task<ActionResult> Post(SignUp command)
    {
        command = command with {UserId = Guid.NewGuid()};
        await _signUpHandler.HandleAsync(command);
        return CreatedAtAction(nameof(Get), new {command.UserId}, null);
    }
    
    [HttpPost("sign-in")]
    public async Task<ActionResult<JwtDto>> Post(SignIn command)
    {
        await _signInHandler.HandleAsync(command);
        var jwt = _tokenStorage.Get();
        return jwt;
    } 
}