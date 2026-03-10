using System.Collections.Concurrent;
using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Web.Services;

public sealed record DataDictionaryTextItemInput(string Value);
public sealed record DataDictionaryImageItemInput(string AssetPath, string FileName);

/// <summary>
/// 数据字典业务服务。
/// </summary>
public sealed class DataDictionaryService
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> QueueLocks = new();

    private readonly IDataDictionaryRepository _dictionaryRepository;
    private readonly IDataDictionaryItemRepository _itemRepository;
    private readonly ImageAssetStorageService _assetStorage;

    public DataDictionaryService(
        IDataDictionaryRepository dictionaryRepository,
        IDataDictionaryItemRepository itemRepository,
        ImageAssetStorageService assetStorage)
    {
        _dictionaryRepository = dictionaryRepository;
        _itemRepository = itemRepository;
        _assetStorage = assetStorage;
    }

    public async Task<IReadOnlyList<DataDictionary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        return await _dictionaryRepository.GetAllWithItemsAsync(cancellationToken);
    }

    public async Task<DataDictionary?> GetAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _dictionaryRepository.GetWithItemsAsync(id, cancellationToken);
    }

    public async Task<DataDictionary?> GetByNameAsync(string name, CancellationToken cancellationToken = default)
    {
        return await _dictionaryRepository.GetByNameAsync(NormalizeName(name), cancellationToken);
    }

    public async Task<DataDictionary> SaveTextDictionaryAsync(
        int? id,
        string name,
        string displayName,
        string? description,
        string readMode,
        bool isEnabled,
        IReadOnlyList<DataDictionaryTextItemInput> items,
        CancellationToken cancellationToken = default)
    {
        name = NormalizeName(name);
        displayName = NormalizeDisplayName(displayName);
        description = NormalizeNullable(description);
        readMode = NormalizeReadMode(readMode);

        if (name.Length == 0)
            throw new InvalidOperationException("字典名称不能为空");
        if (displayName.Length == 0)
            throw new InvalidOperationException("显示名称不能为空");

        var values = (items ?? Array.Empty<DataDictionaryTextItemInput>())
            .Select(x => NormalizeNullable(x.Value))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();

        if (values.Count == 0)
            throw new InvalidOperationException("文本字典至少需要一条内容");

        var entity = await LoadOrCreateAsync(id, DataDictionaryTypes.Text, cancellationToken);
        entity.Name = name;
        entity.DisplayName = displayName;
        entity.Description = description;
        entity.Type = DataDictionaryTypes.Text;
        entity.ReadMode = readMode;
        entity.IsEnabled = isEnabled;
        entity.UpdatedAt = DateTime.UtcNow;
        if (entity.Id == 0)
            entity = await _dictionaryRepository.AddAsync(entity);
        else
            await _dictionaryRepository.UpdateAsync(entity);

        var oldItems = await _itemRepository.GetByDictionaryIdAsync(entity.Id, cancellationToken);
        foreach (var item in oldItems)
            await _itemRepository.DeleteAsync(item);

        for (var index = 0; index < values.Count; index++)
        {
            await _itemRepository.AddAsync(new DataDictionaryItem
            {
                DataDictionaryId = entity.Id,
                TextValue = values[index],
                SortOrder = index,
                IsEnabled = true
            });
        }

        return (await _dictionaryRepository.GetWithItemsAsync(entity.Id, cancellationToken))!;
    }

    public async Task<DataDictionary> SaveImageDictionaryAsync(
        int? id,
        string name,
        string displayName,
        string? description,
        string readMode,
        bool isEnabled,
        IReadOnlyCollection<int> keepItemIds,
        IReadOnlyList<DataDictionaryImageItemInput> newImages,
        CancellationToken cancellationToken = default)
    {
        name = NormalizeName(name);
        displayName = NormalizeDisplayName(displayName);
        description = NormalizeNullable(description);
        readMode = NormalizeReadMode(readMode);

        if (name.Length == 0)
            throw new InvalidOperationException("字典名称不能为空");
        if (displayName.Length == 0)
            throw new InvalidOperationException("显示名称不能为空");

        var entity = await LoadOrCreateAsync(id, DataDictionaryTypes.Image, cancellationToken);
        entity.Name = name;
        entity.DisplayName = displayName;
        entity.Description = description;
        entity.Type = DataDictionaryTypes.Image;
        entity.ReadMode = readMode;
        entity.IsEnabled = isEnabled;
        entity.UpdatedAt = DateTime.UtcNow;
        if (entity.Id == 0)
            entity = await _dictionaryRepository.AddAsync(entity);
        else
            await _dictionaryRepository.UpdateAsync(entity);

        var keepSet = (keepItemIds ?? Array.Empty<int>()).ToHashSet();
        var oldItems = await _itemRepository.GetByDictionaryIdAsync(entity.Id, cancellationToken);
        foreach (var item in oldItems.Where(x => !keepSet.Contains(x.Id)))
        {
            await _assetStorage.DeleteAssetAsync(item.AssetPath, cancellationToken);
            await _itemRepository.DeleteAsync(item);
        }

        var currentItems = await _itemRepository.GetByDictionaryIdAsync(entity.Id, cancellationToken);
        var nextOrder = currentItems.Count == 0 ? 0 : currentItems.Max(x => x.SortOrder) + 1;
        foreach (var image in newImages ?? Array.Empty<DataDictionaryImageItemInput>())
        {
            await _itemRepository.AddAsync(new DataDictionaryItem
            {
                DataDictionaryId = entity.Id,
                AssetPath = NormalizeNullable(image.AssetPath),
                FileName = NormalizeNullable(image.FileName),
                SortOrder = nextOrder++,
                IsEnabled = true
            });
        }

        var finalItems = await _itemRepository.GetByDictionaryIdAsync(entity.Id, cancellationToken);
        if (finalItems.Count == 0)
            throw new InvalidOperationException("图片字典至少需要一张图片");

        return (await _dictionaryRepository.GetWithItemsAsync(entity.Id, cancellationToken))!;
    }

    public async Task SetEnabledAsync(int id, bool enabled, CancellationToken cancellationToken = default)
    {
        var entity = await _dictionaryRepository.GetWithItemsAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("字典不存在");
        entity.IsEnabled = enabled;
        entity.UpdatedAt = DateTime.UtcNow;
        await _dictionaryRepository.UpdateAsync(entity);
    }

    public async Task ResetQueueAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _dictionaryRepository.GetWithItemsAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("字典不存在");
        entity.NextIndex = 0;
        entity.UpdatedAt = DateTime.UtcNow;
        await _dictionaryRepository.UpdateAsync(entity);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var entity = await _dictionaryRepository.GetWithItemsAsync(id, cancellationToken);
        if (entity == null)
            return;

        foreach (var item in entity.Items)
            await _assetStorage.DeleteAssetAsync(item.AssetPath, cancellationToken);

        await _assetStorage.DeleteScopeAsync($"dictionaries/{entity.Id}", cancellationToken);
        await _dictionaryRepository.DeleteAsync(entity);
    }

    public async Task<string> ResolveTextValueAsync(string name, CancellationToken cancellationToken = default)
    {
        var dictionary = await _dictionaryRepository.GetByNameAsync(NormalizeName(name), cancellationToken)
            ?? throw new InvalidOperationException($"字典不存在：{name}");

        if (!dictionary.IsEnabled)
            throw new InvalidOperationException($"字典已停用：{dictionary.Name}");
        if (!string.Equals(dictionary.Type, DataDictionaryTypes.Text, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"字典不是文本类型：{dictionary.Name}");

        var items = dictionary.Items
            .Where(x => x.IsEnabled)
            .Select(x => NormalizeNullable(x.TextValue))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!)
            .ToList();
        if (items.Count == 0)
            throw new InvalidOperationException($"字典没有可用内容：{dictionary.Name}");

        if (string.Equals(dictionary.ReadMode, DataDictionaryReadModes.Queue, StringComparison.OrdinalIgnoreCase))
        {
            var gate = QueueLocks.GetOrAdd(dictionary.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                dictionary = await _dictionaryRepository.GetByNameAsync(dictionary.Name, cancellationToken)
                    ?? throw new InvalidOperationException($"字典不存在：{dictionary.Name}");
                items = dictionary.Items
                    .Where(x => x.IsEnabled)
                    .Select(x => NormalizeNullable(x.TextValue))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .ToList();
                if (items.Count == 0)
                    throw new InvalidOperationException($"字典没有可用内容：{dictionary.Name}");

                var index = dictionary.NextIndex;
                if (index < 0)
                    index = 0;
                var picked = items[index % items.Count];
                dictionary.NextIndex = (index + 1) % items.Count;
                dictionary.UpdatedAt = DateTime.UtcNow;
                await _dictionaryRepository.UpdateAsync(dictionary);
                return picked;
            }
            finally
            {
                gate.Release();
            }
        }

        return items[Random.Shared.Next(items.Count)];
    }

    public async Task<StoredImageAssetInfo> ResolveImageValueAsync(string name, CancellationToken cancellationToken = default)
    {
        var dictionary = await _dictionaryRepository.GetByNameAsync(NormalizeName(name), cancellationToken)
            ?? throw new InvalidOperationException($"字典不存在：{name}");

        if (!dictionary.IsEnabled)
            throw new InvalidOperationException($"字典已停用：{dictionary.Name}");
        if (!string.Equals(dictionary.Type, DataDictionaryTypes.Image, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"字典不是图片类型：{dictionary.Name}");

        var items = dictionary.Items
            .Where(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.AssetPath))
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Id)
            .ToList();
        if (items.Count == 0)
            throw new InvalidOperationException($"字典没有可用图片：{dictionary.Name}");

        if (string.Equals(dictionary.ReadMode, DataDictionaryReadModes.Queue, StringComparison.OrdinalIgnoreCase))
        {
            var gate = QueueLocks.GetOrAdd(dictionary.Id, _ => new SemaphoreSlim(1, 1));
            await gate.WaitAsync(cancellationToken);
            try
            {
                dictionary = await _dictionaryRepository.GetByNameAsync(dictionary.Name, cancellationToken)
                    ?? throw new InvalidOperationException($"字典不存在：{dictionary.Name}");
                items = dictionary.Items
                    .Where(x => x.IsEnabled && !string.IsNullOrWhiteSpace(x.AssetPath))
                    .OrderBy(x => x.SortOrder)
                    .ThenBy(x => x.Id)
                    .ToList();
                if (items.Count == 0)
                    throw new InvalidOperationException($"字典没有可用图片：{dictionary.Name}");

                var index = dictionary.NextIndex;
                if (index < 0)
                    index = 0;
                var picked = items[index % items.Count];
                dictionary.NextIndex = (index + 1) % items.Count;
                dictionary.UpdatedAt = DateTime.UtcNow;
                await _dictionaryRepository.UpdateAsync(dictionary);
                return new StoredImageAssetInfo(picked.AssetPath!, picked.FileName ?? "image.jpg");
            }
            finally
            {
                gate.Release();
            }
        }

        var randomItem = items[Random.Shared.Next(items.Count)];
        return new StoredImageAssetInfo(randomItem.AssetPath!, randomItem.FileName ?? "image.jpg");
    }

    private async Task<DataDictionary> LoadOrCreateAsync(int? id, string type, CancellationToken cancellationToken)
    {
        if (!id.HasValue || id.Value <= 0)
            return new DataDictionary { Type = type, ReadMode = DataDictionaryReadModes.Random, IsEnabled = true };

        var entity = await _dictionaryRepository.GetWithItemsAsync(id.Value, cancellationToken)
            ?? throw new InvalidOperationException("字典不存在");
        if (!string.Equals(entity.Type, type, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("字典类型不匹配，不能跨类型修改");
        return entity;
    }

    private static string NormalizeName(string? value)
    {
        return (value ?? string.Empty).Trim().ToLowerInvariant();
    }

    private static string NormalizeDisplayName(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private static string NormalizeReadMode(string? value)
    {
        return string.Equals((value ?? string.Empty).Trim(), DataDictionaryReadModes.Queue, StringComparison.OrdinalIgnoreCase)
            ? DataDictionaryReadModes.Queue
            : DataDictionaryReadModes.Random;
    }

    private static string? NormalizeNullable(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        return text.Length == 0 ? null : text;
    }
}
