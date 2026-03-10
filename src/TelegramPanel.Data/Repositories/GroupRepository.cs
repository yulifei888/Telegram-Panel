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

    private IQueryable<Group> BuildFilterQuery(int accountId, int? categoryId, string? filterType, string? membershipRole, string? search)
    {
        var query = _dbSet
            .AsNoTracking()
            .AsQueryable();

        query = query.Where(g => g.CreatorAccountId != null || g.AccountGroups.Any());

        membershipRole = (membershipRole ?? "all").Trim().ToLowerInvariant();
        if (accountId > 0)
        {
            query = query.Where(g => g.AccountGroups.Any(x => x.AccountId == accountId));

            if (membershipRole == "creator")
                query = query.Where(g => g.AccountGroups.Any(x => x.AccountId == accountId && x.IsCreator));
            else if (membershipRole == "admin")
                query = query.Where(g => g.AccountGroups.Any(x => x.AccountId == accountId && x.IsAdmin && !x.IsCreator));
            else if (membershipRole == "member")
                query = query.Where(g => g.AccountGroups.Any(x => x.AccountId == accountId && !x.IsAdmin));
        }
        else if (membershipRole == "creator")
        {
            query = query.Where(g => g.AccountGroups.Any(x => x.IsCreator));
        }
        else if (membershipRole == "admin")
        {
            query = query.Where(g => g.AccountGroups.Any(x => x.IsAdmin && !x.IsCreator));
        }
        else if (membershipRole == "member")
        {
            query = query.Where(g => g.AccountGroups.Any(x => !x.IsAdmin));
        }

        if (categoryId.HasValue && categoryId.Value > 0)
            query = query.Where(g => g.CategoryId == categoryId.Value);
        else if (categoryId.HasValue && categoryId.Value == 0)
            query = query.Where(g => g.CategoryId == null);

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
                || (g.Username != null && EF.Functions.Like(g.Username, like))
                || (g.Category != null && EF.Functions.Like(g.Category.Name, like)));
        }

        return query;
    }

    private IQueryable<Group> BuildPagedDetailQuery(IReadOnlyCollection<int> ids, int accountId)
    {
        IQueryable<Group> query = _dbSet
            .AsNoTracking()
            .Where(g => ids.Contains(g.Id))
            .Include(g => g.CreatorAccount)
            .Include(g => g.Category);

        if (accountId > 0)
        {
            query = query
                .Include(g => g.AccountGroups.Where(x => x.AccountId == accountId))
                .AsSplitQuery();
        }

        return query;
    }

    public override async Task<Group?> GetByIdAsync(int id)
    {
        return await _dbSet
            .Include(g => g.CreatorAccount)
            .Include(g => g.Category)
            .Include(g => g.AccountGroups)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.Id == id);
    }

    public override async Task<IEnumerable<Group>> GetAllAsync()
    {
        return await _dbSet
            .Include(g => g.CreatorAccount)
            .Include(g => g.Category)
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
            .Include(g => g.Category)
            .Include(g => g.AccountGroups)
            .AsSplitQuery()
            .FirstOrDefaultAsync(g => g.TelegramId == telegramId);
    }

    public async Task<IEnumerable<Group>> GetByCreatorAccountAsync(int accountId)
    {
        return await _dbSet
            .Include(g => g.CreatorAccount)
            .Include(g => g.Category)
            .Include(g => g.AccountGroups)
            .AsSplitQuery()
            .Where(g => g.CreatorAccountId == accountId)
            .OrderByDescending(g => g.SyncedAt)
            .ToListAsync();
    }

    public async Task<int> DeleteOrphanedAsync(CancellationToken cancellationToken = default)
    {
        var orphanedGroups = await _dbSet
            .Where(g => g.CreatorAccountId == null && !g.AccountGroups.Any())
            .ToListAsync(cancellationToken);

        if (orphanedGroups.Count == 0)
            return 0;

        _dbSet.RemoveRange(orphanedGroups);
        await SaveChangesWithSqliteLockRetryAsync(cancellationToken);
        return orphanedGroups.Count;
    }

    public async Task<(IReadOnlyList<Group> Items, int TotalCount)> QueryForViewPagedAsync(
        int accountId,
        int? categoryId,
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

        var filteredQuery = BuildFilterQuery(accountId, categoryId, filterType, membershipRole, search);
        var total = await filteredQuery.CountAsync(cancellationToken);

        if (total == 0)
            return (Array.Empty<Group>(), 0);

        var pageIds = await filteredQuery
            .OrderByDescending(g => g.SyncedAt)
            .ThenByDescending(g => g.Id)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .Select(g => g.Id)
            .ToListAsync(cancellationToken);

        if (pageIds.Count == 0)
            return (Array.Empty<Group>(), total);

        var orderMap = pageIds
            .Select((id, index) => new { id, index })
            .ToDictionary(x => x.id, x => x.index);

        var items = await BuildPagedDetailQuery(pageIds, accountId)
            .ToListAsync(cancellationToken);

        var orderedItems = items
            .OrderBy(g => orderMap[g.Id])
            .ToList();

        return (orderedItems, total);
    }
}
