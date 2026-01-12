using Susurri.Modules.Users.Core.Entities;

namespace Susurri.Modules.Users.Core.Abstractions;

public interface IUserRepository
{
    Task<User> GetByIdAsync(Guid id);
    Task<byte[]> GetKeyByUsernameAsync(string username);
    Task AddAsync(User user);
    Task UpdateAsync(User user);
    Task DeleteAsync(User user);
}