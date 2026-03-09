using Microsoft.EntityFrameworkCore;
using TelegramPanel.Data.Entities;

namespace TelegramPanel.Data.Repositories;

/// <summary>
/// 频道仓储实现
/// </summary>
public class ChannelRepository : Repository<Channel>, IChannelRepository
{
    public ChannelRepository(AppDbContext context) : base(context)
    {
    }

    private IQueryable<Channel> BuildFilterQuery(int accountId, int? groupId, string? filterType, string? membershipRole, string? search)
    {
        var query = _dbSet
            .AsNoTracking()
            .AsQueryable();

        query = query.Where(c => c.IsBroadcast);
        query = query.Where(c => c.CreatorAccountId != null || c.AccountChannels.Any());

        membershipRole = (membershipRole ?? "all").Trim().ToLowerInvariant();
        if (accountId > 0)
        {
            query = query.Where(c => c.AccountChannels.Any(x => x.AccountId == accountId));

            if (membershipRole == "creator")
                query = query.Where(c => c.AccountChannels.Any(x => x.AccountId == accountId && x.IsCreator));
            else if (membershipRole == "admin")
                query = query.Where(c => c.AccountChannels.Any(x => x.AccountId == accountId && x.IsAdmin && !x.IsCreator));
            else if (membershipRole == "member")
                query = query.Where(c => c.AccountChannels.Any(x => x.AccountId == accountId && !x.IsAdmin));
        }
        else if (membershipRole == "creator")
        {
            query = query.Where(c => c.AccountChannels.Any(x => x.IsCreator));
        }
        else if (membershipRole == "admin")
        {
            query = query.Where(c => c.AccountChannels.Any(x => x.IsAdmin && !x.IsCreator));
        }
        else if (membershipRole == "member")
        {
            query = query.Where(c => c.AccountChannels.Any(x => !x.IsAdmin));
        }

        if (groupId.HasValue && groupId.Value > 0)
            query = query.Where(c => c.GroupId == groupId.Value);
        else if (groupId.HasValue && groupId.Value == 0)
            query = query.Where(c => c.GroupId == null);

        filterType = (filterType ?? "all").Trim().ToLowerInvariant();
        if (filterType == "public")
            query = query.Where(c => c.Username != null && c.Username != "");
        else if (filterType == "private")
            query = query.Where(c => c.Username == null || c.Username == "");

        search = (search ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var like = $"%{search}%";
            query = query.Where(c =>
                EF.Functions.Like(c.Title, like)
                || (c.Username != null && EF.Functions.Like(c.Username, like))
                || (c.Group != null && EF.Functions.Like(c.Group.Name, like)));
        }

        return query;
    }

    private IQueryable<Channel> BuildPagedDetailQuery(IReadOnlyCollection<int> ids, int accountId)
    {
        IQueryable<Channel> query = _dbSet
            .AsNoTracking()
            .Where(c => ids.Contains(c.Id))
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group);

        if (accountId > 0)
        {
            query = query
                .Include(c => c.AccountChannels.Where(x => x.AccountId == accountId))
                .AsSplitQuery();
        }

        return query;
    }

    public override async Task<Channel?> GetByIdAsync(int id)
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Include(c => c.AccountChannels)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Id == id);
    }

    public override async Task<IEnumerable<Channel>> GetAllAsync()
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Include(c => c.AccountChannels)
            .AsSplitQuery()
            .Where(c => c.IsBroadcast)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<Channel?> GetByTelegramIdAsync(long telegramId)
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Include(c => c.AccountChannels)
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.TelegramId == telegramId);
    }

    public async Task<IEnumerable<Channel>> GetCreatedAsync()
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Include(c => c.AccountChannels)
            .AsSplitQuery()
            .Where(c => c.IsBroadcast && c.CreatorAccountId != null)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Channel>> GetByCreatorAccountAsync(int accountId)
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Include(c => c.AccountChannels)
            .AsSplitQuery()
            .Where(c => c.IsBroadcast && c.CreatorAccountId == accountId)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Channel>> GetForAccountAsync(int accountId, bool includeNonCreator)
    {
        var links = _context.Set<AccountChannel>()
            .Where(x => x.AccountId == accountId && (includeNonCreator || x.IsCreator));

        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Include(c => c.AccountChannels)
            .AsSplitQuery()
            .Where(c => c.IsBroadcast && links.Any(x => x.ChannelId == c.Id))
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Channel>> GetByGroupAsync(int groupId)
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Include(c => c.AccountChannels)
            .AsSplitQuery()
            .Where(c => c.IsBroadcast && c.GroupId == groupId)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<IEnumerable<Channel>> GetBroadcastChannelsAsync()
    {
        return await _dbSet
            .Include(c => c.CreatorAccount)
            .Include(c => c.Group)
            .Include(c => c.AccountChannels)
            .AsSplitQuery()
            .Where(c => c.IsBroadcast)
            .OrderByDescending(c => c.SyncedAt)
            .ToListAsync();
    }

    public async Task<int> DeleteOrphanedAsync(CancellationToken cancellationToken = default)
    {
        var orphanedChannels = await _dbSet
            .Where(c => c.CreatorAccountId == null && !c.AccountChannels.Any())
            .ToListAsync(cancellationToken);

        if (orphanedChannels.Count == 0)
            return 0;

        _dbSet.RemoveRange(orphanedChannels);
        await SaveChangesWithSqliteLockRetryAsync(cancellationToken);
        return orphanedChannels.Count;
    }

    public async Task<(IReadOnlyList<Channel> Items, int TotalCount)> QueryForViewPagedAsync(
        int accountId,
        int? groupId,
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

        var filteredQuery = BuildFilterQuery(accountId, groupId, filterType, membershipRole, search);
        var total = await filteredQuery.CountAsync(cancellationToken);

        if (total == 0)
            return (Array.Empty<Channel>(), 0);

        var pageIds = await filteredQuery
            .OrderByDescending(c => c.SyncedAt)
            .ThenByDescending(c => c.Id)
            .Skip(pageIndex * pageSize)
            .Take(pageSize)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);

        if (pageIds.Count == 0)
            return (Array.Empty<Channel>(), total);

        var orderMap = pageIds
            .Select((id, index) => new { id, index })
            .ToDictionary(x => x.id, x => x.index);

        var items = await BuildPagedDetailQuery(pageIds, accountId)
            .ToListAsync(cancellationToken);

        var orderedItems = items
            .OrderBy(c => orderMap[c.Id])
            .ToList();

        return (orderedItems, total);
    }
}
