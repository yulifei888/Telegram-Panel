using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 群组分类仓储实现
/// </summary>
public class GroupCategoryRepository : Repository<GroupCategory>, IGroupCategoryRepository
{
    public GroupCategoryRepository(AppDbContext context) : base(context)
    {
    }

    public override async Task<IEnumerable<GroupCategory>> GetAllAsync()
    {
        return await _dbSet
            .Include(g => g.Groups)
            .OrderBy(g => g.Name)
            .ToListAsync();
    }

    public async Task<GroupCategory?> GetByNameAsync(string name)
    {
        return await _dbSet
            .Include(g => g.Groups)
            .FirstOrDefaultAsync(g => g.Name == name);
    }
}
