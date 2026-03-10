using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace TelegramPanel.Core.Services.Telegram;

/// <summary>
/// Telegram 图片处理工具。
/// </summary>
public static class TelegramImageProcessor
{
    public static async Task<MemoryStream> PrepareAvatarJpegAsync(Stream fileStream, CancellationToken cancellationToken = default)
    {
        if (fileStream == null)
            throw new ArgumentNullException(nameof(fileStream));

        await using var raw = new MemoryStream();
        if (fileStream.CanSeek)
            fileStream.Position = 0;

        await fileStream.CopyToAsync(raw, cancellationToken);
        raw.Position = 0;

        using var image = await Image.LoadAsync(raw, cancellationToken);
        image.Mutate(x => x.AutoOrient());
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Mode = ResizeMode.Crop,
            Size = new Size(512, 512)
        }));

        var encoded = new MemoryStream();
        await image.SaveAsJpegAsync(encoded, new JpegEncoder { Quality = 85 }, cancellationToken);
        encoded.Position = 0;
        return encoded;
    }

    public static async Task<MemoryStream> PrepareStoredImageJpegAsync(
        Stream fileStream,
        int maxDimension = 2048,
        CancellationToken cancellationToken = default)
    {
        if (fileStream == null)
            throw new ArgumentNullException(nameof(fileStream));

        if (maxDimension < 256)
            maxDimension = 256;

        await using var raw = new MemoryStream();
        if (fileStream.CanSeek)
            fileStream.Position = 0;

        await fileStream.CopyToAsync(raw, cancellationToken);
        raw.Position = 0;

        using var image = await Image.LoadAsync(raw, cancellationToken);
        image.Mutate(x => x.AutoOrient());

        if (image.Width > maxDimension || image.Height > maxDimension)
        {
            image.Mutate(x => x.Resize(new ResizeOptions
            {
                Mode = ResizeMode.Max,
                Size = new Size(maxDimension, maxDimension)
            }));
        }

        var encoded = new MemoryStream();
        await image.SaveAsJpegAsync(encoded, new JpegEncoder { Quality = 88 }, cancellationToken);
        encoded.Position = 0;
        return encoded;
    }
}
