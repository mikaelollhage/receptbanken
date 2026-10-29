using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Receptbanken.Core.Interfaces;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Receptbanken.Infrastructure.Storage;

public class ImageStorageOptions
{
    public string BaseDataPath { get; set; } = "data";
    public string ImageFolderName { get; set; } = "images/recipes";
    public int MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024; // 10 MB
    public int MaxImageDimension { get; set; } = 1920;
}

public class LocalImageStorageService : IImageStorageService
{
    private readonly ImageStorageOptions _options;
    private readonly ILogger<LocalImageStorageService> _logger;
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp"
    };

    public LocalImageStorageService(IOptions<ImageStorageOptions> options, ILogger<LocalImageStorageService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsValidImageExtension(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        var ext = Path.GetExtension(fileName);
        return AllowedExtensions.Contains(ext);
    }

    public bool ValidateImageBytes(ReadOnlySpan<byte> headerBytes)
    {
        if (headerBytes.Length < 12) return false;

        // JPEG: FF D8 FF
        if (headerBytes[0] == 0xFF && headerBytes[1] == 0xD8 && headerBytes[2] == 0xFF)
            return true;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (headerBytes[0] == 0x89 && headerBytes[1] == 0x50 && headerBytes[2] == 0x4E && headerBytes[3] == 0x47 &&
            headerBytes[4] == 0x0D && headerBytes[5] == 0x0A && headerBytes[6] == 0x1A && headerBytes[7] == 0x0A)
            return true;

        // WEBP: RIFF .... WEBP
        if (headerBytes[0] == 0x52 && headerBytes[1] == 0x49 && headerBytes[2] == 0x46 && headerBytes[3] == 0x46 &&
            headerBytes[8] == 0x57 && headerBytes[9] == 0x45 && headerBytes[10] == 0x42 && headerBytes[11] == 0x50)
            return true;

        return false;
    }

    public async Task<string> SaveImageAsync(Stream stream, string originalFileName, CancellationToken cancellationToken = default)
    {
        if (!IsValidImageExtension(originalFileName))
        {
            throw new ArgumentException("Otillåtet bildformat. Endast JPG, PNG och WebP stöds.");
        }

        if (stream.CanSeek && stream.Length > _options.MaxFileSizeBytes)
        {
            throw new InvalidOperationException($"Bildfilen överskred maxgränsen ({_options.MaxFileSizeBytes / (1024 * 1024)} MB).");
        }

        // Read header for magic bytes verification
        var header = new byte[16];
        int bytesRead = await stream.ReadAsync(header, 0, header.Length, cancellationToken);
        if (bytesRead < 12 || !ValidateImageBytes(header.AsSpan(0, bytesRead)))
        {
            throw new InvalidOperationException("Filens innehåll matchar inte ett giltigt bildformat.");
        }

        // Reset stream position or buffer into MemoryStream with size limit check
        if (stream.CanSeek)
        {
            stream.Position = 0;
        }
        else
        {
            var memoryStream = new MemoryStream();
            memoryStream.Write(header, 0, bytesRead);
            var buffer = new byte[8192];
            int read;
            int totalBytes = bytesRead;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                totalBytes += read;
                if (totalBytes > _options.MaxFileSizeBytes)
                {
                    memoryStream.Dispose();
                    throw new InvalidOperationException($"Bildfilen överskred maxgränsen ({_options.MaxFileSizeBytes / (1024 * 1024)} MB).");
                }
                memoryStream.Write(buffer, 0, read);
            }
            memoryStream.Position = 0;
            stream = memoryStream;
        }

        var fullDirectory = Path.Combine(_options.BaseDataPath, _options.ImageFolderName);
        Directory.CreateDirectory(fullDirectory);

        var uniqueFileName = $"{Guid.NewGuid():N}.webp";
        var fullPath = Path.Combine(fullDirectory, uniqueFileName);

        using (var image = await Image.LoadAsync(stream, cancellationToken))
        {
            // Resize if too large
            if (image.Width > _options.MaxImageDimension || image.Height > _options.MaxImageDimension)
            {
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    Size = new Size(_options.MaxImageDimension, _options.MaxImageDimension),
                    Mode = ResizeMode.Max
                }));
            }

            var encoder = new WebpEncoder
            {
                Quality = 80
            };

            await image.SaveAsync(fullPath, encoder, cancellationToken);
        }

        _logger.LogInformation("Sparade receptbild: {FileName}", uniqueFileName);
        // Return relative path from base data folder for portable storage
        return Path.Combine(_options.ImageFolderName, uniqueFileName).Replace('\\', '/');
    }

    public Task DeleteImageAsync(string? imagePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return Task.CompletedTask;

        try
        {
            var baseFullPath = Path.GetFullPath(_options.BaseDataPath);
            var fullPath = Path.GetFullPath(Path.Combine(_options.BaseDataPath, imagePath));

            // Prevent path traversal outside base data directory
            if (!fullPath.StartsWith(baseFullPath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Avvisade otillåten filsökväg (path traversal) vid bildborttagning: {Path}", imagePath);
                return Task.CompletedTask;
            }

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
                _logger.LogInformation("Raderade bildfil: {Path}", fullPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Kunde inte radera bildfil: {Path}", imagePath);
        }

        return Task.CompletedTask;
    }
}

