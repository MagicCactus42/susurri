using Microsoft.EntityFrameworkCore;
using Susurri.Modules.Users.Core.Abstractions;
using Susurri.Modules.Users.Core.Entities;

namespace Susurri.Modules.Users.Core.DAL.Repositories;

public class UserRepository : IUserRepository
{
    private readonly UsersDbContext _context;

    public UserRepository(UsersDbContext context)
    {
        _context = context;
    }

    public Task<User?> GetByIdAsync(Guid id)
        => _context.Users.SingleOrDefaultAsync(x => x.UserId.Value == id);

    public Task<byte[]?> GetKeyByUsernameAsync(string username)
        => _context.Users.Where(x => x.Username == username).Select(x => x.PublicKey).FirstOrDefaultAsync();

    public async Task AddAsync(User user)
    {
        await _context.Users.AddAsync(user).ConfigureAwait(false);
        await _context.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task UpdateAsync(User user)
    {
        _context.Users.Update(user);
        await _context.SaveChangesAsync().ConfigureAwait(false);
    }

    public async Task DeleteAsync(User user)
    {
        _context.Users.Remove(user);
        await _context.SaveChangesAsync().ConfigureAwait(false);
    }
}