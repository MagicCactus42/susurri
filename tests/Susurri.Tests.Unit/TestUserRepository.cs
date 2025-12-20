using Susurri.Modules.Users.Core.Abstractions;
using Susurri.Modules.Users.Core.Entities;

namespace Susurri.Tests.Unit;

internal class TestUserRepository : IUserRepository
{
    private readonly List<User> _users = new();

    public Task<User> GetByIdAsync(Guid id)
    {
        var user = _users.SingleOrDefault(x => x.UserId.Value == id);
        return Task.FromResult(user);
    }

    public Task<byte[]> GetKeyByUsernameAsync(string username)
    {
        var key = _users
            .FirstOrDefault(x => x.Username == username)?
            .PublicKey;

        return Task.FromResult(key);
    }

    public Task AddAsync(User user)
    {
        if (_users.Any(x => x.UserId == user.UserId))
            throw new InvalidOperationException("User with this ID already exists.");

        _users.Add(user);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(User user)
    {
        var index = _users.FindIndex(x => x.UserId == user.UserId);
        if (index == -1)
            throw new InvalidOperationException("User not found.");

        _users[index] = user;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(User user)
    {
        var removed = _users.RemoveAll(x => x.UserId == user.UserId);
        if (removed == 0)
            throw new InvalidOperationException("User not found.");

        return Task.CompletedTask;
    }
}