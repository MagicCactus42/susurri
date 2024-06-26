using Microsoft.EntityFrameworkCore;
using Susurri.Core.Abstractions;
using Susurri.Core.DTO;
using Susurri.Core.Queries;
using Susurri.Core.ValueObjects;

namespace Susurri.Core.DAL.Handlers;

internal sealed class GetUserHandler(SusurriDbContext dbContext) : IQueryHandler<GetUser, UserDto>
{
    public async Task<UserDto> HandleAsync(GetUser query)
    {
        var userId = new UserId(query.UserId);
        var user = await dbContext.Users
            .AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == userId);

        return user?.AsDto();
    }
}