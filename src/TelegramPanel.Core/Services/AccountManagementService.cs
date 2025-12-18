using TelegramPanel.Data.Entities;
using TelegramPanel.Data.Repositories;

namespace TelegramPanel.Core.Services;

/// <summary>
/// 账号数据管理服务
/// </summary>
public class AccountManagementService
{
    private readonly IAccountRepository _accountRepository;
    private readonly IChannelRepository _channelRepository;
    private readonly IGroupRepository _groupRepository;

    public AccountManagementService(
        IAccountRepository accountRepository,
        IChannelRepository channelRepository,
        IGroupRepository groupRepository)
    {
        _accountRepository = accountRepository;
        _channelRepository = channelRepository;
        _groupRepository = groupRepository;
    }

    public async Task<Account?> GetAccountAsync(int id)
    {
        return await _accountRepository.GetByIdAsync(id);
    }

    public async Task<Account?> GetAccountByPhoneAsync(string phone)
    {
        return await _accountRepository.GetByPhoneAsync(phone);
    }

    public async Task<IEnumerable<Account>> GetAllAccountsAsync()
    {
        return await _accountRepository.GetAllAsync();
    }

    public async Task<IEnumerable<Account>> GetActiveAccountsAsync()
    {
        return await _accountRepository.GetActiveAccountsAsync();
    }

    public async Task<IEnumerable<Account>> GetAccountsByCategoryAsync(int categoryId)
    {
        return await _accountRepository.GetByCategoryAsync(categoryId);
    }

    public async Task<Account> CreateAccountAsync(Account account)
    {
        return await _accountRepository.AddAsync(account);
    }

    public async Task UpdateAccountAsync(Account account)
    {
        await _accountRepository.UpdateAsync(account);
    }

    public async Task DeleteAccountAsync(int id)
    {
        var account = await _accountRepository.GetByIdAsync(id);
        if (account != null)
        {
            await _accountRepository.DeleteAsync(account);
        }
    }

    public async Task<(int channels, int groups)> GetAccountStatisticsAsync(int accountId)
    {
        var channels = await _channelRepository.GetByCreatorAccountAsync(accountId);
        var groups = await _groupRepository.GetByCreatorAccountAsync(accountId);

        return (channels.Count(), groups.Count());
    }

    public async Task SetAccountActiveStatusAsync(int accountId, bool isActive)
    {
        var account = await _accountRepository.GetByIdAsync(accountId);
        if (account != null)
        {
            account.IsActive = isActive;
            await _accountRepository.UpdateAsync(account);
        }
    }

    public async Task UpdateLastSyncTimeAsync(int accountId)
    {
        var account = await _accountRepository.GetByIdAsync(accountId);
        if (account != null)
        {
            account.LastSyncAt = DateTime.UtcNow;
            await _accountRepository.UpdateAsync(account);
        }
    }
}
