using Microsoft.EntityFrameworkCore;
using Sarab.Api.Domain;

namespace Sarab.Api.Data;

public sealed class SarabDbContext(DbContextOptions<SarabDbContext> options) : DbContext(options)
{
    public DbSet<PromptPackEntity> PromptPacks => Set<PromptPackEntity>();
    public DbSet<PromptCategoryEntity> PromptCategories => Set<PromptCategoryEntity>();
    public DbSet<PromptRoundEntity> PromptRounds => Set<PromptRoundEntity>();
    public DbSet<AnswerFrequencyEntity> AnswerFrequencies => Set<AnswerFrequencyEntity>();
    public DbSet<GameSummaryEntity> GameSummaries => Set<GameSummaryEntity>();
    public DbSet<AvatarPartEntity> AvatarParts => Set<AvatarPartEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<PromptPackEntity>(entity =>
        {
            entity.ToTable("prompt_packs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.Property(x => x.Language).HasMaxLength(16).IsRequired();
            entity.HasMany(x => x.Categories).WithOne(x => x.Pack).HasForeignKey(x => x.PackId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PromptCategoryEntity>(entity =>
        {
            entity.ToTable("prompt_categories");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExternalId).HasMaxLength(120).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(160).IsRequired();
            entity.HasIndex(x => new { x.PackId, x.ExternalId }).IsUnique();
            entity.HasMany(x => x.Rounds).WithOne(x => x.Category).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PromptRoundEntity>(entity =>
        {
            entity.ToTable("prompt_rounds");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExternalId).HasMaxLength(120).IsRequired();
            entity.Property(x => x.PromptA).HasMaxLength(120).IsRequired();
            entity.Property(x => x.PromptB).HasMaxLength(120).IsRequired();
            entity.Property(x => x.ObviousAnswersJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.CategoryId, x.ExternalId }).IsUnique();
        });

        modelBuilder.Entity<AnswerFrequencyEntity>(entity =>
        {
            entity.ToTable("answer_frequencies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.NormalizedAnswer).HasMaxLength(120).IsRequired();
            entity.HasIndex(x => new { x.RoundId, x.PromptIndex, x.NormalizedAnswer }).IsUnique();
        });

        modelBuilder.Entity<GameSummaryEntity>(entity =>
        {
            entity.ToTable("game_summaries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.RoomCode).HasMaxLength(12).IsRequired();
            entity.Property(x => x.CategoryName).HasMaxLength(160).IsRequired();
            entity.Property(x => x.SummaryJson).HasColumnType("jsonb");
        });

        modelBuilder.Entity<AvatarPartEntity>(entity =>
        {
            entity.ToTable("avatar_parts");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Type).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(120).IsRequired();
            entity.Property(x => x.ImageUrl).HasMaxLength(1024).IsRequired();
            entity.Property(x => x.MaleTransformJson).HasColumnType("jsonb");
            entity.Property(x => x.FemaleTransformJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.Type, x.Active });
        });
    }
}

public sealed class PromptPackEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string Language { get; set; } = "en";
    public bool Active { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<PromptCategoryEntity> Categories { get; set; } = [];
}

public sealed class PromptCategoryEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid PackId { get; set; }
    public PromptPackEntity? Pack { get; set; }
    public string ExternalId { get; set; } = "";
    public string Name { get; set; } = "";
    public List<PromptRoundEntity> Rounds { get; set; } = [];
}

public sealed class PromptRoundEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CategoryId { get; set; }
    public PromptCategoryEntity? Category { get; set; }
    public string ExternalId { get; set; } = "";
    public string PromptA { get; set; } = "";
    public string PromptB { get; set; } = "";
    public int Closeness { get; set; }
    public string ObviousAnswersJson { get; set; } = "{}";
}

public sealed class AnswerFrequencyEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RoundId { get; set; }
    public int PromptIndex { get; set; }
    public string NormalizedAnswer { get; set; } = "";
    public int Count { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class GameSummaryEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string RoomCode { get; set; } = "";
    public DateTimeOffset FinishedAt { get; set; } = DateTimeOffset.UtcNow;
    public string CategoryName { get; set; } = "";
    public string SummaryJson { get; set; } = "{}";
}

public sealed class AvatarPartEntity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Type { get; set; } = "clothes";
    public string Name { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public bool SupportsMale { get; set; }
    public bool SupportsFemale { get; set; }
    public bool Active { get; set; } = true;
    public string? MaleTransformJson { get; set; }
    public string? FemaleTransformJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
