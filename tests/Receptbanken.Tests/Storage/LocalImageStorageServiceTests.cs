using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Receptbanken.Infrastructure.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Receptbanken.Tests.Storage;

public class LocalImageStorageServiceTests : IDisposable
{
    private readonly string _testTempDir;
    private readonly LocalImageStorageService _service;

    public LocalImageStorageServiceTests()
    {
        _testTempDir = Path.Combine(Path.GetTempPath(), $"receptbanken_img_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testTempDir);

        var options = Options.Create(new ImageStorageOptions
        {
            BaseDataPath = _testTempDir,
            ImageFolderName = "images/recipes",
            MaxImageDimension = 800
        });

        _service = new LocalImageStorageService(options, NullLogger<LocalImageStorageService>.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testTempDir))
            {
                Directory.Delete(_testTempDir, true);
            }
        }
        catch { }
    }

    [Theory]
    [InlineData("mat.jpg", true)]
    [InlineData("mat.jpeg", true)]
    [InlineData("mat.png", true)]
    [InlineData("mat.webp", true)]
    [InlineData("MAT.PNG", true)]
    [InlineData("skadlig.exe", false)]
    [InlineData("vektor.svg", false)]
    [InlineData("dokument.pdf", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidImageExtension_ShouldValidateAllowedFormats(string? fileName, bool expected)
    {
        var result = _service.IsValidImageExtension(fileName!);
        result.Should().Be(expected);
    }

    [Fact]
    public void ValidateImageBytes_ShouldAcceptValidHeaders()
    {
        // JPEG header: FF D8 FF E0 ...
        byte[] jpegHeader = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 };
        _service.ValidateImageBytes(jpegHeader).Should().BeTrue();

        // PNG header: 89 50 4E 47 0D 0A 1A 0A ...
        byte[] pngHeader = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D };
        _service.ValidateImageBytes(pngHeader).Should().BeTrue();

        // WEBP header: 52 49 46 46 (len) 57 45 42 50
        byte[] webpHeader = { 0x52, 0x49, 0x46, 0x46, 0x24, 0x00, 0x00, 0x00, 0x57, 0x45, 0x42, 0x50 };
        _service.ValidateImageBytes(webpHeader).Should().BeTrue();
    }

    [Fact]
    public void ValidateImageBytes_ShouldRejectInvalidHeaders()
    {
        // Plain text
        byte[] textBytes = System.Text.Encoding.UTF8.GetBytes("Detta är en vanlig textfil");
        _service.ValidateImageBytes(textBytes).Should().BeFalse();

        // Too short
        byte[] shortBytes = { 0xFF, 0xD8 };
        _service.ValidateImageBytes(shortBytes).Should().BeFalse();
    }

    [Fact]
    public async Task SaveImageAsync_ShouldConvertAndSaveAsWebP()
    {
        // Arrange: create a small test PNG image in memory using ImageSharp
        using var memoryStream = new MemoryStream();
        using (var image = new Image<Rgba32>(100, 100))
        {
            await image.SaveAsPngAsync(memoryStream);
        }
        memoryStream.Position = 0;

        // Act
        var relativePath = await _service.SaveImageAsync(memoryStream, "test.png");

        // Assert
        relativePath.Should().EndWith(".webp");
        var fullPath = Path.Combine(_testTempDir, relativePath);
        File.Exists(fullPath).Should().BeTrue();

        // Verify it can be loaded as a valid ImageSharp image
        using var loaded = await Image.LoadAsync(fullPath);
        loaded.Width.Should().Be(100);
        loaded.Height.Should().Be(100);
    }

    [Fact]
    public async Task DeleteImageAsync_ShouldRemoveFileFromDisk()
    {
        // Arrange
        using var memoryStream = new MemoryStream();
        using (var image = new Image<Rgba32>(50, 50))
        {
            await image.SaveAsPngAsync(memoryStream);
        }
        memoryStream.Position = 0;

        var relativePath = await _service.SaveImageAsync(memoryStream, "del.png");
        var fullPath = Path.Combine(_testTempDir, relativePath);
        File.Exists(fullPath).Should().BeTrue();

        // Act
        await _service.DeleteImageAsync(relativePath);

        // Assert
        File.Exists(fullPath).Should().BeFalse();
    }

    [Fact]
    public async Task SaveImageAsync_OversizedStream_ShouldThrowInvalidOperationException()
    {
        // 11 MB stream
        var fakeHeader = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00 };
        var oversizedLength = 11 * 1024 * 1024;
        using var stream = new FakeOversizedStream(fakeHeader, oversizedLength);

        var act = async () => await _service.SaveImageAsync(stream, "large.jpg");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*överskred maxgränsen*");
    }

    [Fact]
    public async Task DeleteImageAsync_PathTraversal_ShouldSafelyBlockDeletionOutsideBaseDir()
    {
        // Create a sensitive file outside images directory in temp root
        var sensitiveFile = Path.Combine(_testTempDir, "sensitive.txt");
        await File.WriteAllTextAsync(sensitiveFile, "do not delete");

        // Attempt path traversal with ..
        await _service.DeleteImageAsync("../../sensitive.txt");

        // Sensitive file must not be deleted
        File.Exists(sensitiveFile).Should().BeTrue();
    }

    [Fact]
    public async Task SaveImageAsync_InvalidFileExtension_ShouldThrowArgumentException()
    {
        using var stream = new MemoryStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01 });
        var act = async () => await _service.SaveImageAsync(stream, "dangerous.svg");

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*Otillåtet bildformat*");
    }

    [Fact]
    public async Task SaveImageAsync_ValidExtensionWithInvalidBytes_ShouldThrowInvalidOperationException()
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Not a valid JPEG content"));
        var act = async () => await _service.SaveImageAsync(stream, "fake.jpg");

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Filens innehåll matchar inte ett giltigt bildformat*");
    }

    private sealed class FakeOversizedStream : Stream
    {
        private readonly byte[] _header;
        private readonly long _length;
        private long _position;

        public FakeOversizedStream(byte[] header, long length)
        {
            _header = header;
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length) return 0;
            int bytesRead = 0;
            while (count > 0 && _position < _length)
            {
                if (_position < _header.Length)
                {
                    buffer[offset++] = _header[_position++];
                }
                else
                {
                    buffer[offset++] = 0;
                    _position++;
                }
                bytesRead++;
                count--;
            }
            return bytesRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => _position
            };
            return _position;
        }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

