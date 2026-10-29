namespace Receptbanken.Core.Interfaces;

public interface IImageStorageService
{
    Task<string> SaveImageAsync(Stream stream, string originalFileName, CancellationToken cancellationToken = default);
    Task DeleteImageAsync(string? imagePath, CancellationToken cancellationToken = default);
    bool IsValidImageExtension(string fileName);
    bool ValidateImageBytes(ReadOnlySpan<byte> headerBytes);
}

