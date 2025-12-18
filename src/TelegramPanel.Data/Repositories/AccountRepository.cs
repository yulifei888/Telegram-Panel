using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 账号仓储实现
/// </summary>
public class AccountRepository : Repository<Account>, IAccountRepository
{
    public AccountRepository(AppDbContext context) : base(context)
    {
    }

    public override async Task<Account?> GetByIdAsync(int id)
    {
        return await _dbSet
            .Include(a => a.Category)
            .Include(a => a.Channels)
            .Include(a => a.Groups)
            .FirstOrDefaultAsync(a => a.Id == id);
    }

    public override async Task<IEnumerable<Account>> GetAllAsync()
    {
        return await _dbSet
            .Include(a => a.Category)
            .ToListAsync();
    }

    public async Task<Account?> GetByPhoneAsync(string phone)
    {
        return await _dbSet
            .Include(a => a.Category)
            .FirstOrDefaultAsync(a => a.Phone == phone);
    }

    public async Task<Account?> GetByUserIdAsync(long userId)
    {
        return await _dbSet
            .Include(a => a.Category)
            .FirstOrDefaultAsync(a => a.UserId == userId);
    }

    public async Task<IEnumerable<Account>> GetByCategoryAsync(int categoryId)
    {
        return await _dbSet
            .Include(a => a.Category)
            .Where(a => a.CategoryId == categoryId)
            .ToListAsync();
    }

    public async Task<IEnumerable<Account>> GetActiveAccountsAsync()
    {
        return await _dbSet
            .Include(a => a.Category)
            .Where(a => a.IsActive)
            .ToListAsync();
    }
}
