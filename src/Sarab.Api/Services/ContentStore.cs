using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Sarab.Api.Data;
using Sarab.Api.Domain;

namespace Sarab.Api.Services;

public interface IContentStore
{
    Task<IReadOnlyList<CatalogCategoryDto>> GetCatalogAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AdminPackDto>> GetAdminPacksAsync(CancellationToken cancellationToken = default);
    Task<PromptCategory?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default);
    Task<PromptPack> SavePackAsync(PromptPackUpload upload, CancellationToken cancellationToken = default);
    Task SetPackActiveAsync(Guid packId, bool active, CancellationToken cancellationToken = default);
    Task DeletePackAsync(Guid packId, CancellationToken cancellationToken = default);
    Task<int> GetRoundPlayCountAsync(Guid roundId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, int>> GetAnswerFrequenciesAsync(Guid roundId, int promptIndex, CancellationToken cancellationToken = default);
    Task RecordAnswersAsync(Guid roundId, IReadOnlyList<(int PromptIndex, string Answer)> answers, CancellationToken cancellationToken = default);
    Task SaveGameSummaryAsync(GameSummaryDto summary, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<GameSummaryDto>> GetRecentSummariesAsync(string roomCode, CancellationToken cancellationToken = default);
    Task DeleteHistoryAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AvatarPartDto>> GetAvatarPartsAsync(bool activeOnly, CancellationToken cancellationToken = default);
    Task<AvatarPartDto> SaveAvatarPartAsync(AvatarPartDto part, CancellationToken cancellationToken = default);
    Task<AvatarPartDto?> UpdateAvatarPartAsync(Guid partId, AvatarPartUpdateRequest update, CancellationToken cancellationToken = default);
    Task DeleteAvatarPartAsync(Guid partId, CancellationToken cancellationToken = default);
}

public sealed class InMemoryContentStore : IContentStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PromptPack> _packs = [];
    private readonly Dictionary<Guid, AvatarPartDto> _avatarParts = [];
    private readonly Dictionary<(Guid RoundId, int PromptIndex, string Answer), int> _frequencies = [];
    private readonly List<GameSummaryDto> _summaries = [];

    public InMemoryContentStore()
    {
        var seed = SeedPackFactory.Create();
        _packs[seed.Id] = seed;
        foreach (var part in AvatarSeedFactory.CreateParts())
        {
            _avatarParts[part.Id] = part;
        }
    }

    public Task<IReadOnlyList<CatalogCategoryDto>> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<CatalogCategoryDto>>(
                _packs.Values
                    .Where(pack => pack.Active)
                    .SelectMany(pack => pack.Categories.Select(category => new CatalogCategoryDto(
                        category.Id,
                        pack.Id,
                        pack.Name,
                        category.Name,
                        PromptPackValidator.FormatLanguage(pack.Language),
                        category.Rounds.Count)))
                    .OrderBy(x => x.PackName)
                    .ThenBy(x => x.CategoryName)
                    .ToList());
        }
    }

    public Task<IReadOnlyList<AdminPackDto>> GetAdminPacksAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AdminPackDto>>(_packs.Values.Select(ToAdminDto).ToList());
        }
    }

    public Task<PromptCategory?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_packs.Values.SelectMany(pack => pack.Categories).FirstOrDefault(x => x.Id == categoryId));
        }
    }

    public Task<PromptPack> SavePackAsync(PromptPackUpload upload, CancellationToken cancellationToken = default)
    {
        var pack = ToPack(upload);
        lock (_gate)
        {
            _packs[pack.Id] = pack;
        }

        return Task.FromResult(pack);
    }

    public Task SetPackActiveAsync(Guid packId, bool active, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_packs.TryGetValue(packId, out var pack))
            {
                _packs[packId] = pack with { Active = active };
            }
        }

        return Task.CompletedTask;
    }

    public Task DeletePackAsync(Guid packId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _packs.Remove(packId);
        }

        return Task.CompletedTask;
    }

    public Task<int> GetRoundPlayCountAsync(Guid roundId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_frequencies.Where(x => x.Key.RoundId == roundId).Sum(x => x.Value));
        }
    }

    public Task<IReadOnlyDictionary<string, int>> GetAnswerFrequenciesAsync(Guid roundId, int promptIndex, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var result = _frequencies
                .Where(x => x.Key.RoundId == roundId && x.Key.PromptIndex == promptIndex)
                .ToDictionary(x => x.Key.Answer, x => x.Value, StringComparer.Ordinal);
            return Task.FromResult<IReadOnlyDictionary<string, int>>(result);
        }
    }

    public Task RecordAnswersAsync(Guid roundId, IReadOnlyList<(int PromptIndex, string Answer)> answers, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var answer in answers)
            {
                var normalized = TextTools.NormalizeAnswer(answer.Answer);
                var key = (roundId, answer.PromptIndex, normalized);
                _frequencies[key] = _frequencies.GetValueOrDefault(key) + 1;
            }
        }

        return Task.CompletedTask;
    }

    public Task SaveGameSummaryAsync(GameSummaryDto summary, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _summaries.Insert(0, summary);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<GameSummaryDto>> GetRecentSummariesAsync(string roomCode, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<GameSummaryDto>>(
                _summaries.Where(x => x.RoomCode == roomCode).Take(10).ToList());
        }
    }

    public Task DeleteHistoryAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _frequencies.Clear();
            _summaries.Clear();
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<AvatarPartDto>> GetAvatarPartsAsync(bool activeOnly, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<AvatarPartDto>>(_avatarParts.Values
                .Where(part => !activeOnly || part.Active)
                .OrderBy(part => part.Type)
                .ThenBy(part => part.Name)
                .ToList());
        }
    }

    public Task<AvatarPartDto> SaveAvatarPartAsync(AvatarPartDto part, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _avatarParts[part.Id] = part;
        }

        return Task.FromResult(part);
    }

    public Task<AvatarPartDto?> UpdateAvatarPartAsync(Guid partId, AvatarPartUpdateRequest update, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_avatarParts.TryGetValue(partId, out var existing))
            {
                return Task.FromResult<AvatarPartDto?>(null);
            }

            var updated = existing with
            {
                Type = update.Type,
                Name = update.Name.Trim(),
                SupportsMale = update.SupportsMale,
                SupportsFemale = update.SupportsFemale,
                Active = update.Active,
                MaleTransform = update.SupportsMale ? NormalizeTransform(update.MaleTransform) : null,
                FemaleTransform = update.SupportsFemale ? NormalizeTransform(update.FemaleTransform) : null
            };
            _avatarParts[partId] = updated;
            return Task.FromResult<AvatarPartDto?>(updated);
        }
    }

    public Task DeleteAvatarPartAsync(Guid partId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _avatarParts.Remove(partId);
        }

        return Task.CompletedTask;
    }

    private static AdminPackDto ToAdminDto(PromptPack pack) => new(
        pack.Id,
        pack.Name,
        PromptPackValidator.FormatLanguage(pack.Language),
        pack.Active,
        pack.Categories.Count,
        pack.Categories.Sum(x => x.Rounds.Count));

    internal static PromptPack ToPack(PromptPackUpload upload)
    {
        var packId = Guid.NewGuid();
        var language = PromptPackValidator.ParseLanguage(upload.Language);
        var categories = upload.Categories.Select(category =>
        {
            var categoryId = Guid.NewGuid();
            var rounds = category.Rounds.Select(round => new PromptRound(
                Guid.NewGuid(),
                round.Id.Trim(),
                categoryId,
                round.Prompts[0].Trim(),
                round.Prompts[1].Trim(),
                round.Closeness,
                ToObviousAnswers(round.ObviousAnswers))).ToList();

            return new PromptCategory(categoryId, category.Id.Trim(), category.Name.Trim(), packId, language, rounds);
        }).ToList();

        return new PromptPack(packId, upload.Name.Trim(), language, true, categories);
    }

    internal static IReadOnlyDictionary<int, string[]> ToObviousAnswers(Dictionary<string, string[]>? obviousAnswers)
    {
        var result = new Dictionary<int, string[]>();
        if (obviousAnswers is null)
        {
            return result;
        }

        foreach (var pair in obviousAnswers)
        {
            if (int.TryParse(pair.Key, out var key) && key is 0 or 1)
            {
                result[key] = pair.Value.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToArray();
            }
        }

        return result;
    }

    internal static AvatarLayerTransformDto NormalizeTransform(AvatarLayerTransformDto? transform)
    {
        var value = transform ?? new AvatarLayerTransformDto(0, 0, 1, 0);
        return new AvatarLayerTransformDto(
            Math.Clamp(value.X, -320, 320),
            Math.Clamp(value.Y, -320, 320),
            Math.Clamp(value.Scale, 0.2m, 2.5m),
            Math.Clamp(value.Rotation, -180, 180));
    }
}

public sealed class DbContentStore(SarabDbContext db) : IContentStore
{
    public async Task<IReadOnlyList<CatalogCategoryDto>> GetCatalogAsync(CancellationToken cancellationToken = default)
    {
        return await db.PromptCategories
            .AsNoTracking()
            .Where(x => x.Pack!.Active)
            .Select(x => new CatalogCategoryDto(
                x.Id,
                x.PackId,
                x.Pack!.Name,
                x.Name,
                x.Pack.Language,
                x.Rounds.Count))
            .OrderBy(x => x.PackName)
            .ThenBy(x => x.CategoryName)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AdminPackDto>> GetAdminPacksAsync(CancellationToken cancellationToken = default)
    {
        return await db.PromptPacks
            .AsNoTracking()
            .Select(x => new AdminPackDto(
                x.Id,
                x.Name,
                x.Language,
                x.Active,
                x.Categories.Count,
                x.Categories.SelectMany(c => c.Rounds).Count()))
            .OrderBy(x => x.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<PromptCategory?> GetCategoryAsync(Guid categoryId, CancellationToken cancellationToken = default)
    {
        var entity = await db.PromptCategories
            .AsNoTracking()
            .Include(x => x.Pack)
            .Include(x => x.Rounds)
            .FirstOrDefaultAsync(x => x.Id == categoryId && x.Pack!.Active, cancellationToken);

        if (entity is null || entity.Pack is null)
        {
            return null;
        }

        var language = PromptPackValidator.ParseLanguage(entity.Pack.Language);
        return new PromptCategory(
            entity.Id,
            entity.ExternalId,
            entity.Name,
            entity.PackId,
            language,
            entity.Rounds.Select(ToRound).ToList());
    }

    public async Task<PromptPack> SavePackAsync(PromptPackUpload upload, CancellationToken cancellationToken = default)
    {
        var pack = InMemoryContentStore.ToPack(upload);
        var entity = new PromptPackEntity
        {
            Id = pack.Id,
            Name = pack.Name,
            Language = PromptPackValidator.FormatLanguage(pack.Language),
            Active = pack.Active,
            Categories = pack.Categories.Select(category => new PromptCategoryEntity
            {
                Id = category.Id,
                ExternalId = category.ExternalId,
                Name = category.Name,
                Rounds = category.Rounds.Select(round => new PromptRoundEntity
                {
                    Id = round.Id,
                    ExternalId = round.ExternalId,
                    PromptA = round.PromptA,
                    PromptB = round.PromptB,
                    Closeness = round.Closeness,
                    ObviousAnswersJson = JsonSerializer.Serialize(round.ObviousAnswers)
                }).ToList()
            }).ToList()
        };

        db.PromptPacks.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return pack;
    }

    public async Task SetPackActiveAsync(Guid packId, bool active, CancellationToken cancellationToken = default)
    {
        var pack = await db.PromptPacks.FindAsync([packId], cancellationToken);
        if (pack is null)
        {
            return;
        }

        pack.Active = active;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task DeletePackAsync(Guid packId, CancellationToken cancellationToken = default)
    {
        var pack = await db.PromptPacks.FindAsync([packId], cancellationToken);
        if (pack is null)
        {
            return;
        }

        db.PromptPacks.Remove(pack);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<int> GetRoundPlayCountAsync(Guid roundId, CancellationToken cancellationToken = default)
    {
        return await db.AnswerFrequencies
            .Where(x => x.RoundId == roundId)
            .SumAsync(x => x.Count, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, int>> GetAnswerFrequenciesAsync(Guid roundId, int promptIndex, CancellationToken cancellationToken = default)
    {
        return await db.AnswerFrequencies
            .AsNoTracking()
            .Where(x => x.RoundId == roundId && x.PromptIndex == promptIndex)
            .ToDictionaryAsync(x => x.NormalizedAnswer, x => x.Count, StringComparer.Ordinal, cancellationToken);
    }

    public async Task RecordAnswersAsync(Guid roundId, IReadOnlyList<(int PromptIndex, string Answer)> answers, CancellationToken cancellationToken = default)
    {
        foreach (var answer in answers)
        {
            var normalized = TextTools.NormalizeAnswer(answer.Answer);
            var existing = await db.AnswerFrequencies.FirstOrDefaultAsync(
                x => x.RoundId == roundId && x.PromptIndex == answer.PromptIndex && x.NormalizedAnswer == normalized,
                cancellationToken);

            if (existing is null)
            {
                db.AnswerFrequencies.Add(new AnswerFrequencyEntity
                {
                    RoundId = roundId,
                    PromptIndex = answer.PromptIndex,
                    NormalizedAnswer = normalized,
                    Count = 1
                });
            }
            else
            {
                existing.Count++;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task SaveGameSummaryAsync(GameSummaryDto summary, CancellationToken cancellationToken = default)
    {
        db.GameSummaries.Add(new GameSummaryEntity
        {
            Id = summary.Id,
            RoomCode = summary.RoomCode,
            FinishedAt = summary.FinishedAt,
            CategoryName = summary.CategoryName,
            SummaryJson = JsonSerializer.Serialize(summary)
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GameSummaryDto>> GetRecentSummariesAsync(string roomCode, CancellationToken cancellationToken = default)
    {
        var entities = await db.GameSummaries
            .AsNoTracking()
            .Where(x => x.RoomCode == roomCode)
            .OrderByDescending(x => x.FinishedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        return entities
            .Select(x => JsonSerializer.Deserialize<GameSummaryDto>(x.SummaryJson))
            .OfType<GameSummaryDto>()
            .ToList();
    }

    public async Task DeleteHistoryAsync(CancellationToken cancellationToken = default)
    {
        await db.AnswerFrequencies.ExecuteDeleteAsync(cancellationToken);
        await db.GameSummaries.ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AvatarPartDto>> GetAvatarPartsAsync(bool activeOnly, CancellationToken cancellationToken = default)
    {
        var query = db.AvatarParts.AsNoTracking();
        if (activeOnly)
        {
            query = query.Where(x => x.Active);
        }

        var entities = await query
            .OrderBy(x => x.Type)
            .ThenBy(x => x.Name)
            .ToListAsync(cancellationToken);
        return entities.Select(ToAvatarPart).ToList();
    }

    public async Task<AvatarPartDto> SaveAvatarPartAsync(AvatarPartDto part, CancellationToken cancellationToken = default)
    {
        db.AvatarParts.Add(new AvatarPartEntity
        {
            Id = part.Id,
            Type = FormatAvatarPartType(part.Type),
            Name = part.Name,
            ImageUrl = part.ImageUrl,
            SupportsMale = part.SupportsMale,
            SupportsFemale = part.SupportsFemale,
            Active = part.Active,
            MaleTransformJson = part.MaleTransform is null ? null : JsonSerializer.Serialize(part.MaleTransform),
            FemaleTransformJson = part.FemaleTransform is null ? null : JsonSerializer.Serialize(part.FemaleTransform)
        });
        await db.SaveChangesAsync(cancellationToken);
        return part;
    }

    public async Task<AvatarPartDto?> UpdateAvatarPartAsync(Guid partId, AvatarPartUpdateRequest update, CancellationToken cancellationToken = default)
    {
        var entity = await db.AvatarParts.FindAsync([partId], cancellationToken);
        if (entity is null)
        {
            return null;
        }

        entity.Type = FormatAvatarPartType(update.Type);
        entity.Name = update.Name.Trim();
        entity.SupportsMale = update.SupportsMale;
        entity.SupportsFemale = update.SupportsFemale;
        entity.Active = update.Active;
        entity.MaleTransformJson = update.SupportsMale ? JsonSerializer.Serialize(InMemoryContentStore.NormalizeTransform(update.MaleTransform)) : null;
        entity.FemaleTransformJson = update.SupportsFemale ? JsonSerializer.Serialize(InMemoryContentStore.NormalizeTransform(update.FemaleTransform)) : null;
        await db.SaveChangesAsync(cancellationToken);
        return ToAvatarPart(entity);
    }

    public async Task DeleteAvatarPartAsync(Guid partId, CancellationToken cancellationToken = default)
    {
        var entity = await db.AvatarParts.FindAsync([partId], cancellationToken);
        if (entity is null)
        {
            return;
        }

        db.AvatarParts.Remove(entity);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static PromptRound ToRound(PromptRoundEntity entity)
    {
        var obvious = JsonSerializer.Deserialize<Dictionary<int, string[]>>(entity.ObviousAnswersJson) ?? [];
        return new PromptRound(entity.Id, entity.ExternalId, entity.CategoryId, entity.PromptA, entity.PromptB, entity.Closeness, obvious);
    }

    private static AvatarPartDto ToAvatarPart(AvatarPartEntity entity)
    {
        return new AvatarPartDto(
            entity.Id,
            ParseAvatarPartType(entity.Type),
            entity.Name,
            entity.ImageUrl,
            entity.SupportsMale,
            entity.SupportsFemale,
            entity.Active,
            DeserializeTransform(entity.MaleTransformJson),
            DeserializeTransform(entity.FemaleTransformJson));
    }

    private static AvatarLayerTransformDto? DeserializeTransform(string? json)
    {
        return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<AvatarLayerTransformDto>(json);
    }

    private static string FormatAvatarPartType(AvatarPartType type) => type switch
    {
        AvatarPartType.Clothes => "clothes",
        AvatarPartType.Face => "face",
        AvatarPartType.Headwear => "headwear",
        _ => "clothes"
    };

    private static AvatarPartType ParseAvatarPartType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "face" => AvatarPartType.Face,
        "headwear" => AvatarPartType.Headwear,
        _ => AvatarPartType.Clothes
    };
}

internal static class AvatarSeedFactory
{
    private static readonly Guid ClothesId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid FaceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid HeadwearId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    public static IReadOnlyList<AvatarPartDto> CreateParts()
    {
        var transform = new AvatarLayerTransformDto(0, 0, 1, 0);
        return
        [
            new AvatarPartDto(ClothesId, AvatarPartType.Clothes, "Starter thobe", SvgDataUrl("<path d='M365 490 Q512 420 659 490 L720 930 Q512 1000 304 930 Z' fill='%23269f9b'/><path d='M448 482 L512 710 L576 482' stroke='white' stroke-width='22' fill='none' stroke-linecap='round'/>"), true, true, true, transform, transform),
            new AvatarPartDto(FaceId, AvatarPartType.Face, "Warm smile", SvgDataUrl("<circle cx='430' cy='360' r='18' fill='%23231920'/><circle cx='594' cy='360' r='18' fill='%23231920'/><path d='M442 445 Q512 510 582 445' stroke='%23231920' stroke-width='18' fill='none' stroke-linecap='round'/>"), true, true, true, transform, transform),
            new AvatarPartDto(HeadwearId, AvatarPartType.Headwear, "Desert cap", SvgDataUrl("<path d='M350 248 Q512 142 674 248 L642 328 Q512 276 382 328 Z' fill='%23e9c400'/><path d='M382 328 Q512 372 642 328' stroke='%236b6054' stroke-width='16' fill='none'/>"), true, true, true, transform, transform)
        ];
    }

    private static string SvgDataUrl(string body)
    {
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 1024 1024'>{body}</svg>";
        return $"data:image/svg+xml;utf8,{Uri.EscapeDataString(svg)}";
    }
}

internal static class SeedPackFactory
{
    public static PromptPack Create()
    {
        var upload = new PromptPackUpload(
            1,
            "en",
            "Sarab Starter Pack",
            [
                new CategoryUpload(
                    "family-table",
                    "Family Table",
                    [
                        new PromptRoundUpload("ocean-lake", ["Ocean", "Lake"], 82, new()
                        {
                            ["0"] = ["water", "blue", "waves"],
                            ["1"] = ["water", "fish", "shore"]
                        }),
                        new PromptRoundUpload("coffee-tea", ["Coffee", "Tea"], 76, new()
                        {
                            ["0"] = ["drink", "beans", "morning"],
                            ["1"] = ["drink", "leaves", "hot"]
                        }),
                        new PromptRoundUpload("moon-star", ["Moon", "Star"], 68, new()
                        {
                            ["0"] = ["night", "sky", "light"],
                            ["1"] = ["night", "sky", "shine"]
                        }),
                        new PromptRoundUpload("mountain-hill", ["Mountain", "Hill"], 88, new()
                        {
                            ["0"] = ["high", "snow", "climb"],
                            ["1"] = ["green", "small", "climb"]
                        }),
                        new PromptRoundUpload("phone-tablet", ["Phone", "Tablet"], 79, new()
                        {
                            ["0"] = ["screen", "call", "app"],
                            ["1"] = ["screen", "ipad", "app"]
                        })
                    ])
            ]);

        return InMemoryContentStore.ToPack(upload);
    }
}
