using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Receptbanken.Core.Models;

namespace Receptbanken.Infrastructure.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));
    }

    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<Ingredient> Ingredients => Set<Ingredient>();
    public DbSet<InstructionStep> Instructions => Set<InstructionStep>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<RecipeCategory> RecipeCategories => Set<RecipeCategory>();
    public DbSet<ShoppingListItem> ShoppingListItems => Set<ShoppingListItem>();
    public DbSet<WeeklyMenu> WeeklyMenus => Set<WeeklyMenu>();
    public DbSet<WeeklyMenuDay> WeeklyMenuDays => Set<WeeklyMenuDay>();
    public DbSet<WeeklyMenuDayCategory> WeeklyMenuDayCategories => Set<WeeklyMenuDayCategory>();
    public DbSet<WeeklyMenuPantryItem> WeeklyMenuPantryItems => Set<WeeklyMenuPantryItem>();
    public DbSet<WeeklyMenuShoppingContribution> WeeklyMenuShoppingContributions => Set<WeeklyMenuShoppingContribution>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite cannot ORDER BY DateTimeOffset. Store as UTC ticks (INTEGER) so that
        // sorting and comparisons are translated server-side and remain correct.
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<UtcTicksDateTimeOffsetConverter>();
    }

    private sealed class UtcTicksDateTimeOffsetConverter : ValueConverter<DateTimeOffset, long>
    {
        public UtcTicksDateTimeOffsetConverter()
            : base(
                v => v.UtcTicks,
                v => new DateTimeOffset(v, TimeSpan.Zero))
        {
        }
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Recipe
        modelBuilder.Entity<Recipe>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Title).IsRequired().HasMaxLength(200);
            entity.Property(e => e.SourceUrl).HasMaxLength(2000);
            entity.Property(e => e.SourceHost).HasMaxLength(100);
            entity.Property(e => e.ImagePath).HasMaxLength(500);
            entity.Property(e => e.ServingsUnit).IsRequired().HasMaxLength(50);
            entity.HasIndex(e => e.Title);
            entity.HasIndex(e => e.IsFavorite);

            entity.HasMany(e => e.Ingredients)
                .WithOne(e => e.Recipe)
                .HasForeignKey(e => e.RecipeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.Instructions)
                .WithOne(e => e.Recipe)
                .HasForeignKey(e => e.RecipeId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Ingredient
        modelBuilder.Entity<Ingredient>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RawText).IsRequired();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Unit).HasMaxLength(30);
            entity.Property(e => e.Comment).HasMaxLength(200);
            entity.Property(e => e.Preparation).HasMaxLength(200);
            entity.Property(e => e.GroupHeader).HasMaxLength(100);
            entity.Property(e => e.Quantity).HasPrecision(10, 2);
            entity.Property(e => e.QuantityMax).HasPrecision(10, 2);
        });

        // InstructionStep
        modelBuilder.Entity<InstructionStep>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.InstructionText).IsRequired();
            entity.Property(e => e.GroupHeader).HasMaxLength(100);
        });

        // Category
        modelBuilder.Entity<Category>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.Name).IsRequired().HasMaxLength(100);
            entity.Property(e => e.ColorHex).HasMaxLength(9);
            entity.HasIndex(e => e.Name).IsUnique();
        });

        // RecipeCategory (Join Table)
        modelBuilder.Entity<RecipeCategory>(entity =>
        {
            entity.HasKey(rc => new { rc.RecipeId, rc.CategoryId });

            entity.HasOne(rc => rc.Recipe)
                .WithMany(r => r.RecipeCategories)
                .HasForeignKey(rc => rc.RecipeId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(rc => rc.Category)
                .WithMany(c => c.RecipeCategories)
                .HasForeignKey(rc => rc.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ShoppingListItem
        modelBuilder.Entity<ShoppingListItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.RawText).IsRequired();
            entity.Property(e => e.Name).IsRequired().HasMaxLength(200);
            entity.Property(e => e.Unit).HasMaxLength(30);
            entity.Property(e => e.Comment).HasMaxLength(500);
            entity.Property(e => e.Department).HasMaxLength(100);
            entity.Property(e => e.Quantity).HasPrecision(10, 2);
            entity.Property(e => e.QuantityMax).HasPrecision(10, 2);
            entity.Property(e => e.SortOrder).HasDefaultValue(0);

            entity.HasOne(e => e.Recipe)
                .WithMany()
                .HasForeignKey(e => e.RecipeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // WeeklyMenu
        modelBuilder.Entity<WeeklyMenu>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => e.WeekStartDate).IsUnique();
            entity.HasIndex(e => new { e.IsoYear, e.IsoWeek }).IsUnique();
            entity.HasIndex(e => e.Status);

            entity.HasMany(e => e.Days)
                .WithOne(d => d.WeeklyMenu)
                .HasForeignKey(d => d.WeeklyMenuId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.PantryItems)
                .WithOne(p => p.WeeklyMenu)
                .HasForeignKey(p => p.WeeklyMenuId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasMany(e => e.ShoppingContributions)
                .WithOne(c => c.WeeklyMenu)
                .HasForeignKey(c => c.WeeklyMenuId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // WeeklyMenuDay
        modelBuilder.Entity<WeeklyMenuDay>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.HasIndex(e => new { e.WeeklyMenuId, e.DayIndex }).IsUnique();
            entity.HasIndex(e => new { e.WeeklyMenuId, e.Date }).IsUnique();

            entity.HasOne(e => e.Recipe)
                .WithMany()
                .HasForeignKey(e => e.RecipeId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        // WeeklyMenuDayCategory (Join Table)
        modelBuilder.Entity<WeeklyMenuDayCategory>(entity =>
        {
            entity.HasKey(dc => new { dc.WeeklyMenuDayId, dc.CategoryId });

            entity.HasOne(dc => dc.WeeklyMenuDay)
                .WithMany(d => d.SelectedCategories)
                .HasForeignKey(dc => dc.WeeklyMenuDayId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(dc => dc.Category)
                .WithMany()
                .HasForeignKey(dc => dc.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // WeeklyMenuPantryItem
        modelBuilder.Entity<WeeklyMenuPantryItem>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.IngredientName).IsRequired().HasMaxLength(150);
        });

        // WeeklyMenuShoppingContribution
        modelBuilder.Entity<WeeklyMenuShoppingContribution>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.ContributionKey).IsRequired().HasMaxLength(250);
            entity.Property(e => e.Unit).HasMaxLength(30);
            entity.Property(e => e.AddedQuantity).HasPrecision(10, 2);
            entity.Property(e => e.AddedQuantityMax).HasPrecision(10, 2);
            entity.HasIndex(e => new { e.WeeklyMenuId, e.DayIndex, e.RecipeId, e.ContributionKey }).IsUnique();
            entity.HasIndex(e => e.WeekStartDate);
        });
    }
}

