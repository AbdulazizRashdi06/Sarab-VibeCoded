using Sarab.Api.Domain;
using Sarab.Api.Services;

namespace Sarab.Tests;

public sealed class GameRuleTests
{
    [Fact]
    public void Validator_rejects_bad_round_shape()
    {
        var validator = new PromptPackValidator();
        var result = validator.Validate(new PromptPackUpload(
            1,
            "en",
            "Bad",
            [
                new CategoryUpload(
                    "x",
                    "Broken",
                    [new PromptRoundUpload("same", ["Ocean", "Ocean"], 110, new() { ["2"] = ["bad"] })])
            ]));

        Assert.False(result.Valid);
        Assert.Contains(result.Errors, x => x.Contains("must be different", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, x => x.Contains("between 0 and 100", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, x => x.Contains("keys must be", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Waves", "wave")]
    [InlineData("أمواج", "امواج")]
    [InlineData("قهوة!", "قهوه")]
    public void Normalizer_catches_english_and_arabic_variants(string left, string right)
    {
        Assert.True(TextTools.NearlyEqual(left, right));
    }

    [Fact]
    public async Task Scoring_clamps_penalties_at_zero()
    {
        var store = new InMemoryContentStore();
        var round = await FirstRound(store);
        var players = Players(score: 10);
        var answers = Answers(players, ["water", "water", "water", "shore"], imposterIndex: 3);

        var outcome = await new ScoringEngine(store).ScoreAsync(new ScoringInput(
            1,
            0,
            15,
            DateTimeOffset.UtcNow,
            round,
            round.PromptA,
            round.PromptB,
            players[3].Id,
            players,
            answers,
            [new PlayerVote(players[0].Id, answers[1].AnswerId, ConfidenceLevel.High, DateTimeOffset.UtcNow)],
            [],
            []));

        Assert.All(outcome.Scores.Values, score => Assert.True(score >= 0));
        Assert.Contains(outcome.Result.Events, e =>
            e.Reason == "Wrong confidence vote"
            && e.Detail.Contains("High confidence wrong-vote penalty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Undetected_imposter_gets_half_pool_and_rollover_keeps_rest()
    {
        var store = new InMemoryContentStore();
        var round = await FirstRound(store);
        var players = Players();
        var answers = Answers(players, ["blue", "waves", "fish", "shore"], imposterIndex: 3);

        var outcome = await new ScoringEngine(store).ScoreAsync(new ScoringInput(
            1,
            0,
            15,
            DateTimeOffset.UtcNow,
            round,
            round.PromptA,
            round.PromptB,
            players[3].Id,
            players,
            answers,
            [
                new PlayerVote(players[0].Id, answers[1].AnswerId, ConfidenceLevel.Low, DateTimeOffset.UtcNow),
                new PlayerVote(players[1].Id, answers[0].AnswerId, ConfidenceLevel.Low, DateTimeOffset.UtcNow),
                new PlayerVote(players[2].Id, answers[0].AnswerId, ConfidenceLevel.Low, DateTimeOffset.UtcNow)
            ],
            [],
            []));

        Assert.True(outcome.Scores[players[3].Id] > 100);
        Assert.True(outcome.Result.Rollover > 0);
        Assert.Contains(outcome.Result.Highlights, x => x.Contains("rolled over", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Confidence_values_are_harsh_for_high_confidence()
    {
        Assert.Equal(0, ScoringEngine.VotePenalty(ConfidenceLevel.Low));
        Assert.Equal(20, ScoringEngine.VotePenalty(ConfidenceLevel.Medium));
        Assert.Equal(60, ScoringEngine.VotePenalty(ConfidenceLevel.High));
        Assert.True(ScoringEngine.ConfidenceWeight(ConfidenceLevel.High) > ScoringEngine.ConfidenceWeight(ConfidenceLevel.Medium));
    }

    [Fact]
    public async Task Safe_bet_rewards_safe_players_and_penalizes_the_mirage()
    {
        var store = new InMemoryContentStore();
        var round = await FirstRound(store);
        var players = Players();
        var answers = Answers(players, ["blue", "waves", "fish", "shore"], imposterIndex: 3);

        var outcome = await new ScoringEngine(store).ScoreAsync(new ScoringInput(
            1,
            0,
            15,
            DateTimeOffset.UtcNow,
            round,
            round.PromptA,
            round.PromptB,
            players[3].Id,
            players,
            answers,
            [],
            [],
            [
                new SafeBetClaim(players[0].Id, DateTimeOffset.UtcNow),
                new SafeBetClaim(players[3].Id, DateTimeOffset.UtcNow)
            ]));

        Assert.Contains(outcome.Result.Events, e => e.PlayerId == players[0].Id && e.Reason == "Correct safe bet" && e.Delta == ScoringEngine.SafeBetBonus);
        Assert.Contains(outcome.Result.Events, e => e.PlayerId == players[3].Id && e.Reason == "Wrong safe bet" && e.Delta == -ScoringEngine.WrongSafeBetPenalty);
    }

    [Fact]
    public void History_threshold_is_per_prompt_pair_at_100_plays()
    {
        Assert.Equal(100, ScoringEngine.HistoryThreshold);
    }

    private static async Task<PromptRound> FirstRound(InMemoryContentStore store)
    {
        var category = (await store.GetCatalogAsync())[0];
        var full = await store.GetCategoryAsync(category.CategoryId);
        return full!.Rounds[0];
    }

    private static List<ScoringPlayer> Players(int score = 100)
    {
        return Enumerable.Range(0, 4)
            .Select(i => new ScoringPlayer(Guid.NewGuid(), $"P{i + 1}", score))
            .ToList();
    }

    private static List<SubmittedAnswer> Answers(IReadOnlyList<ScoringPlayer> players, string[] values, int imposterIndex)
    {
        return players.Select((player, index) => new SubmittedAnswer(
            Guid.NewGuid(),
            player.Id,
            values[index],
            index == imposterIndex ? 1 : 0)).ToList();
    }
}
