namespace Receptbanken.Core.Interfaces;

public class BackupManifest
{
    public int FormatVersion { get; set; } = 1;
    public string Application { get; set; } = "Receptbanken";
    public DateTimeOffset ExportedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int CategoryCount { get; set; }
    public int RecipeCount { get; set; }
    public int ShoppingListItemCount { get; set; }
    public int ImageCount { get; set; }
}

public class BackupRestoreResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public int RestoredCategories { get; set; }
    public int RestoredRecipes { get; set; }
    public int RestoredImages { get; set; }
}

public interface IBackupService
{
    Task<byte[]> CreateBackupArchiveAsync(CancellationToken cancellationToken = default);
    Task<BackupRestoreResult> RestoreFromArchiveAsync(
        Stream archiveStream,
        bool replaceExisting = true,
        CancellationToken cancellationToken = default);
}

