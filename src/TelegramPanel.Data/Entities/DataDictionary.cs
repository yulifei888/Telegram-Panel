namespace TelegramPanel.Data.Entities;

/// <summary>
/// 数据字典类型常量。
/// </summary>
public static class DataDictionaryTypes
{
    public const string Text = "text";
    public const string Image = "image";
}

/// <summary>
/// 数据字典读取模式常量。
/// </summary>
public static class DataDictionaryReadModes
{
    public const string Random = "random";
    public const string Queue = "queue";
}

/// <summary>
/// 数据字典。
/// </summary>
public class DataDictionary
{
    public int Id { get; set; }
    public string Name { get; set; } = null!;
    public string DisplayName { get; set; } = null!;
    public string? Description { get; set; }
    public string Type { get; set; } = DataDictionaryTypes.Text;
    public string ReadMode { get; set; } = DataDictionaryReadModes.Random;
    public bool IsEnabled { get; set; } = true;
    public int NextIndex { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public ICollection<DataDictionaryItem> Items { get; set; } = new List<DataDictionaryItem>();
}
