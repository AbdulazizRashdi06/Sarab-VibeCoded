using System.Text.Json.Serialization;

namespace Sarab.Api.Domain;

public enum PackLanguage
{
    En,
    ArOm
}

public enum RoomPhase
{
    Lobby,
    Answer,
    SelfReport,
    Vote,
    Results,
    GameOver
}

public enum ConfidenceLevel
{
    Low,
    Medium,
    High
}

public enum AvatarGender
{
    Male,
    Female
}

public enum AvatarPartType
{
    Clothes,
    Face,
    Headwear
}

public sealed record AvatarLayerTransformDto(decimal X, decimal Y, decimal Scale, decimal Rotation);

public sealed record PlayerAvatarDto(
    AvatarGender Gender,
    string SkinColor,
    Guid? ClothesId,
    Guid? FaceId,
    Guid? HeadwearId);

public sealed record AvatarPartDto(
    Guid Id,
    AvatarPartType Type,
    string Name,
    string ImageUrl,
    bool SupportsMale,
    bool SupportsFemale,
    bool Active,
    AvatarLayerTransformDto? MaleTransform,
    AvatarLayerTransformDto? FemaleTransform);

public sealed record AvatarPartUpdateRequest(
    AvatarPartType Type,
    string Name,
    bool SupportsMale,
    bool SupportsFemale,
    bool Active,
    AvatarLayerTransformDto? MaleTransform,
    AvatarLayerTransformDto? FemaleTransform);

public sealed record PromptPackUpload(
    int SchemaVersion,
    string Language,
    string Name,
    IReadOnlyList<CategoryUpload> Categories);

public sealed record CategoryUpload(
    string Id,
    string Name,
    IReadOnlyList<PromptRoundUpload> Rounds);

public sealed record PromptRoundUpload(
    string Id,
    IReadOnlyList<string> Prompts,
    int Closeness,
    Dictionary<string, string[]>? ObviousAnswers);

public sealed record PromptPack(
    Guid Id,
    string Name,
    PackLanguage Language,
    bool Active,
    IReadOnlyList<PromptCategory> Categories);

public sealed record PromptCategory(
    Guid Id,
    string ExternalId,
    string Name,
    Guid PackId,
    PackLanguage Language,
    IReadOnlyList<PromptRound> Rounds);

public sealed record PromptRound(
    Guid Id,
    string ExternalId,
    Guid CategoryId,
    string PromptA,
    string PromptB,
    int Closeness,
    IReadOnlyDictionary<int, string[]> ObviousAnswers);

public sealed record CatalogCategoryDto(
    Guid CategoryId,
    Guid PackId,
    string PackName,
    string CategoryName,
    string Language,
    int RoundCount);

public sealed record ValidationResultDto(bool Valid, IReadOnlyList<string> Errors);

public sealed record CreateRoomRequest(string PlayerName, string Locale = "en", PlayerAvatarDto? Avatar = null);

public sealed record JoinRoomRequest(string RoomCode, string PlayerName, string Locale = "en", PlayerAvatarDto? Avatar = null);

public sealed record ReconnectRoomRequest(string RoomCode, Guid PlayerId);

public sealed record StartGameRequest(
    Guid CategoryId,
    int TotalRounds,
    int AnswerSeconds = 30,
    int SelfReportSeconds = 15,
    int VoteSeconds = 30);

public sealed record SubmitAnswerRequest(string Answer);

public sealed record SubmitVoteRequest(Guid AnswerId, ConfidenceLevel Confidence);

public sealed record PlayerDto(
    Guid Id,
    string Name,
    int Score,
    PlayerAvatarDto Avatar,
    bool Connected,
    bool IsHost,
    bool IsBot,
    bool Ready,
    bool WaitingForNextRound,
    bool SubmittedAnswer,
    bool SelfReported,
    bool SafeBet,
    bool SelfReportDone,
    bool Voted);

public sealed record AnswerDto(
    Guid Id,
    string Text,
    Guid? AuthorId,
    string? AuthorName,
    bool IsMine,
    bool IsImposterAnswer);

public sealed record ScoreEventDto(Guid PlayerId, string Reason, int Delta, string Detail);

public sealed record RoundResultDto(
    int RoundNumber,
    int Pool,
    int Rollover,
    Guid ImposterPlayerId,
    Guid ImposterAnswerId,
    string MajorityPrompt,
    string AlternatePrompt,
    IReadOnlyList<ScoreEventDto> Events,
    IReadOnlyList<string> Highlights);

public sealed record RoomSnapshotDto(
    string Code,
    RoomPhase Phase,
    Guid? You,
    string? YourPrompt,
    string HostName,
    int CurrentRound,
    int TotalRounds,
    int Rollover,
    DateTimeOffset? PhaseEndsAt,
    IReadOnlyList<PlayerDto> Players,
    IReadOnlyList<AnswerDto> Answers,
    RoundResultDto? LastResult,
    IReadOnlyList<CatalogCategoryDto> Categories,
    string? Warning);

public sealed record AdminPackDto(Guid Id, string Name, string Language, bool Active, int CategoryCount, int RoundCount);

public sealed record GameSummaryDto(
    Guid Id,
    string RoomCode,
    DateTimeOffset FinishedAt,
    string CategoryName,
    IReadOnlyList<PlayerSummaryDto> Players,
    IReadOnlyList<RoundResultDto> Rounds);

public sealed record PlayerSummaryDto(Guid PlayerId, string Name, int Score);

[JsonSerializable(typeof(PromptPackUpload))]
[JsonSerializable(typeof(RoomSnapshotDto))]
[JsonSerializable(typeof(GameSummaryDto))]
internal sealed partial class SarabJsonContext : JsonSerializerContext;
