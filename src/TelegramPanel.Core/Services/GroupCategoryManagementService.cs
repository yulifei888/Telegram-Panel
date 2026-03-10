using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Core.Services;

/// <summary>
/// 群组分类管理服务
/// </summary>
public class GroupCategoryManagementService
{
    private readonly IGroupCategoryRepository _categoryRepository;

    public GroupCategoryManagementService(IGroupCategoryRepository categoryRepository)
    {
        _categoryRepository = categoryRepository;
    }

    public async Task<IEnumerable<GroupCategory>> GetAllCategoriesAsync()
    {
        return await _categoryRepository.GetAllAsync();
    }

    public async Task<GroupCategory?> GetCategoryAsync(int id)
    {
        return await _categoryRepository.GetByIdAsync(id);
    }

    public async Task<GroupCategory?> GetCategoryByNameAsync(string name)
    {
        return await _categoryRepository.GetByNameAsync(name);
    }

    public async Task<GroupCategory> CreateCategoryAsync(GroupCategory category)
    {
        return await _categoryRepository.AddAsync(category);
    }

    public async Task UpdateCategoryAsync(GroupCategory category)
    {
        await _categoryRepository.UpdateAsync(category);
    }

    public async Task DeleteCategoryAsync(int id)
    {
        var category = await _categoryRepository.GetByIdAsync(id);
        if (category != null)
            await _categoryRepository.DeleteAsync(category);
    }
}
