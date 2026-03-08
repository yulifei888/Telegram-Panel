using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 群组仓储实现
/// </summary>
public class GroupRepository : Repository<Group>, IGroupRepository
{
    public GroupRepository(AppDbContext context) : base(context)
    {
    }

    private IQueryable<Group> BuildForViewQuery(int accountId, string? filterType, string? membershipRole, string? search)
    {
        var query = _dbSet
            .AsNoTracking()
            .Include(g => g.CreatorAccount)
            .Include(g => g.AccountGroups)
            .AsSplitQuery()
            .AsQueryable();

        if (accountId > 0)
        {
            query = query.Where(g => g.AccountGroups.Any(x => x.AccountId == accountId));

            membershipRole = (membershipRole ?? "all").Trim().ToLowerInvariant();
            if (membershipRole == "creator")
            {
                query = query.Where(g => g.AccountGroups.Any(x => x.AccountId == accountId && x.IsCreator));
            }
            else if (membershipRole == "admin")
            {
                query = query.Where(g => g.AccountGroups.Any(x => x.AccountId == accountId && x.IsAdmin && !x.IsCreator));
            }
            else if (membershipRole == "member")
            {
                query = query.Where(g => g.AccountGroups.Any(x => x.AccountId == accountId && !x.IsAdmin));
            }
        }
        else
        {
            query = query.Where(g => g.CreatorAccountId != null || g.AccountGroups.Any());
        }

        filterType = (filterType ?? "all").Trim().ToLowerInvariant();
        if (filterType == "public")
            query = query.Where(g => g.Username != null && g.Username != "");
        else if (filterType == "private")
            query = query.Where(g => g.Username == null || g.Username == "");

        search = (search ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = $"%{search}%";
            query = query.Where(g =>
                EF.Functions.Like(g.Title, like)
                || (g.Username != null && EF.Functions.Like(g.Username, like)));
        }

        return query.OrderByDescending(g => g.SyncedAt);
    }

    public override async Task<Group?> GetByIdAsync(int id)
    {
        return await _dbSet
            .Include(g => g.CreatorAccount)
            .Include(g => g.AccountGroups)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == id);
    }

    public override async Task<IEnumerable<Group>> GetAllAsync()
    {
        return await _dbSet
            .Include(g => g.CreatorAccount)
            .Include(g => g.AccountGroups)
            .AsSplitQuery()
            .Where(g => g.CreatorAccountId != null || g.AccountGroups.Any())
            .OrderByDescending(g => g.SyncedAt)
            .ToListAsync();
    }

    public async Task<Group?> GetByTelegramIdAsync(long telegramId)
    {
        return await _dbSet
            .Include(g => g.CreatorAccount)
            .Include(g => g.AccountGroups)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.TelegramId == telegramId);
    }

    public async Task<IEnumerable<Group>> GetByCreatorAccountAsync(int accountId)
    {
        return await _dbSet
            .Include(g => g.CreatorAccount)
            .Include(g => g.AccountGroups)
            .AsSplitQuery()
            .Where(g => g.CreatorAccountId == accountId)
            .OrderByDescending(g => g.SyncedAt)
            .ToListAsync();
    }

    public async Task<(IReadOnlyList<Group> Items, int TotalCount)> QueryForViewPagedAsync(
        int accountId,
        string? filterType,
        string? membershipRole,
        string? search,
        int pageIndex,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0) pageIndex = 0;
        if (pageSize <= 0) pageSize = 20;
        if (pageSize > 500) pageSize = 500;

        var query = BuildForViewQuery(accountId, filterType, membershipRole, search);
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }
}
