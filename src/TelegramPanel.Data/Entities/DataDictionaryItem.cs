namespace TelegramPanel.Data.Entities;

/// <summary>
/// 数据字典项。
/// </summary>
public class DataDictionaryItem
{
    public int Id { get; set; }
    public int DataDictionaryId { get; set; }
    public string? TextValue { get; set; }
    public string? AssetPath { get; set; }
    public string? FileName { get; set; }
    public int SortOrder { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DataDictionary Dictionary { get; set; } = null!;
}
