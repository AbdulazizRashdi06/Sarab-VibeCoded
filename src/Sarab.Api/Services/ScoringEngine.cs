using Sarab.Api.Domain;

namespace Sarab.Api.Services;

public sealed class ScoringEngine(IContentStore contentStore)
{
    public const int StartingScore = 100;
    public const int BaseRoundPool = 100;
    public const int HistoryThreshold = 100;
    public const int SafeBetBonus = 5;
    public const int WrongSafeBetPenalty = 15;

    public async Task<ScoringOutcome> ScoreAsync(ScoringInput input, CancellationToken cancellationToken = default)
    {
        var events = new List<ScoreEventDto>();
        var highlights = new List<string>();
        var scores = input.Players.ToDictionary(x => x.Id, x => x.Score);
        var pool = BaseRoundPool + input.Rollover;

        var imposterAnswer = input.Answers.Single(x => x.PlayerId == input.ImposterPlayerId);
        var correctVotes = input.Votes.Where(x => x.AnswerId == imposterAnswer.AnswerId).OrderBy(x => x.VotedAt).ToList();
        var wrongVotes = input.Votes.Where(x => x.AnswerId != imposterAnswer.AnswerId).ToList();

        foreach (var vote in wrongVotes)
        {
            var penalty = VotePenalty(vote.Confidence);
            if (penalty <= 0)
            {
                continue;
            }

            var realized = Subtract(scores, vote.PlayerId, penalty);
            pool += realized;
            AddEvent(
                events,
                vote.PlayerId,
                "Wrong confidence vote",
                -realized,
                $"{vote.Confidence} confidence wrong-vote penalty: {realized} points moved into the pool.");
        }

        foreach (var claim in input.SelfReports)
        {
            var amount = SelfReportAmount(input.SelfReportWindowSeconds, claim.ClaimedAt - input.SelfReportOpenedAt);
            if (claim.PlayerId == input.ImposterPlayerId)
            {
                Add(scores, claim.PlayerId, amount);
                AddEvent(
                    events,
                    claim.PlayerId,
                    "Correct self-report",
                    amount,
                    $"Correct self-report bonus: {amount} points for claiming during the tell window.");
                highlights.Add($"{input.PlayerName(claim.PlayerId)} called themselves out correctly.");
            }
            else
            {
                var realized = Subtract(scores, claim.PlayerId, amount);
                pool += realized;
                AddEvent(
                    events,
                    claim.PlayerId,
                    "False self-report",
                    -realized,
                    $"False self-report penalty: {realized} points moved into the pool.");
            }
        }

        foreach (var safeBet in input.SafeBets)
        {
            if (safeBet.PlayerId == input.ImposterPlayerId)
            {
                var realized = Subtract(scores, safeBet.PlayerId, WrongSafeBetPenalty);
                pool += realized;
                AddEvent(
                    events,
                    safeBet.PlayerId,
                    "Wrong safe bet",
                    -realized,
                    $"Wrong safe bet penalty: {realized} points moved into the pool because this player was the mirage.");
            }
            else
            {
                Add(scores, safeBet.PlayerId, SafeBetBonus);
                AddEvent(
                    events,
                    safeBet.PlayerId,
                    "Correct safe bet",
                    SafeBetBonus,
                    $"Correct safe bet bonus: {SafeBetBonus} points for betting they were not the mirage.");
            }
        }

        await ApplyAnswerPenaltiesAsync(input, scores, events, penalty =>
        {
            pool += penalty;
        }, cancellationToken);

        var caughtPenalty = correctVotes.Sum(x => ConfidenceWeight(x.Confidence) * 8);
        if (caughtPenalty > 0)
        {
            var realized = Subtract(scores, input.ImposterPlayerId, caughtPenalty);
            pool += realized;
            AddEvent(
                events,
                input.ImposterPlayerId,
                "Caught by confidence votes",
                -realized,
                $"Caught mirage penalty: {correctVotes.Count} correct confidence {Pluralize(correctVotes.Count, "vote")} charged {realized} points.");
            highlights.Add("The table found the mirage.");
        }

        var rollover = 0;
        if (correctVotes.Count == 0)
        {
            var payout = pool / 2;
            rollover = pool - payout;
            Add(scores, input.ImposterPlayerId, payout);
            AddEvent(
                events,
                input.ImposterPlayerId,
                "Undetected imposter payout",
                payout,
                $"Undetected mirage payout: no correct votes, so the imposter took half the pool ({payout}).");
            highlights.Add("Nobody found the alternate prompt. Half the pool rolled over.");
        }
        else
        {
            DistributePool(pool, correctVotes, scores, events);
            highlights.Add($"{correctVotes.Count} {Pluralize(correctVotes.Count, "player")} voted for the imposter answer.");
        }

        await contentStore.RecordAnswersAsync(
            input.Round.Id,
            input.Answers.Select(answer => (answer.PromptIndex, answer.Text)).ToList(),
            cancellationToken);

        var result = new RoundResultDto(
            input.RoundNumber,
            pool,
            rollover,
            input.ImposterPlayerId,
            imposterAnswer.AnswerId,
            input.MajorityPrompt,
            input.AlternatePrompt,
            events,
            highlights);

        return new ScoringOutcome(scores, result);
    }

    private async Task ApplyAnswerPenaltiesAsync(
        ScoringInput input,
        Dictionary<Guid, int> scores,
        List<ScoreEventDto> events,
        Action<int> addToPool,
        CancellationToken cancellationToken)
    {
        var normalized = input.Answers.ToDictionary(x => x.AnswerId, x => TextTools.NormalizeAnswer(x.Text));
        var roundPlayCount = await contentStore.GetRoundPlayCountAsync(input.Round.Id, cancellationToken);

        foreach (var answer in input.Answers)
        {
            var promptIndex = answer.PromptIndex;
            var isObvious = roundPlayCount >= HistoryThreshold
                ? await IsHistoricallyObviousAsync(input.Round.Id, promptIndex, normalized[answer.AnswerId], cancellationToken)
                : IsAdminObvious(input.Round, promptIndex, answer.Text);

            if (!isObvious)
            {
                continue;
            }

            var realized = Subtract(scores, answer.PlayerId, 6);
            addToPool(realized);
            var source = roundPlayCount >= HistoryThreshold ? "answer history frequency" : "admin obvious-answer list";
            AddEvent(
                events,
                answer.PlayerId,
                "Obvious answer",
                -realized,
                $"Obvious answer penalty: matched the {source}, costing {realized} points.");
        }

        var clusters = new List<List<SubmittedAnswer>>();
        foreach (var answer in input.Answers)
        {
            var cluster = clusters.FirstOrDefault(existing => existing.Any(member => TextTools.NearlyEqual(member.Text, answer.Text)));
            if (cluster is null)
            {
                clusters.Add([answer]);
            }
            else
            {
                cluster.Add(answer);
            }
        }

        foreach (var cluster in clusters.Where(x => x.Count > 1))
        {
            foreach (var answer in cluster)
            {
                var realized = Subtract(scores, answer.PlayerId, 4);
                addToPool(realized);
                AddEvent(
                    events,
                    answer.PlayerId,
                    "Copycat/converged answer",
                    -realized,
                    $"Copycat penalty: {cluster.Count} answers converged on a near-match, costing {realized} points.");
            }
        }
    }

    private async Task<bool> IsHistoricallyObviousAsync(Guid roundId, int promptIndex, string normalized, CancellationToken cancellationToken)
    {
        var frequencies = await contentStore.GetAnswerFrequenciesAsync(roundId, promptIndex, cancellationToken);
        if (frequencies.Count == 0)
        {
            return false;
        }

        var total = frequencies.Values.Sum();
        return frequencies.TryGetValue(normalized, out var count) && count >= Math.Max(8, total / 5);
    }

    private static bool IsAdminObvious(PromptRound round, int promptIndex, string answer)
    {
        return round.ObviousAnswers.TryGetValue(promptIndex, out var obvious)
            && obvious.Any(value => TextTools.NearlyEqual(value, answer));
    }

    private static void DistributePool(
        int pool,
        IReadOnlyList<PlayerVote> correctVotes,
        Dictionary<Guid, int> scores,
        List<ScoreEventDto> events)
    {
        var weighted = correctVotes.Select((vote, index) =>
        {
            var speed = Math.Max(0.65, 1.3 - index * 0.12);
            var jitter = 0.95 + StableJitter(vote.PlayerId, vote.AnswerId) * 0.1;
            return new
            {
                vote.PlayerId,
                vote.Confidence,
                Weight = ConfidenceWeight(vote.Confidence) * speed * jitter
            };
        }).ToList();

        var totalWeight = weighted.Sum(x => x.Weight);
        var paid = 0;
        foreach (var item in weighted)
        {
            var payout = (int)Math.Round(pool * item.Weight / totalWeight);
            paid += payout;
            Add(scores, item.PlayerId, payout);
            AddEvent(
                events,
                item.PlayerId,
                "Correct vote payout",
                payout,
                $"Correct {item.Confidence} confidence vote payout: {payout} points from the pool.");
        }

        var drift = pool - paid;
        if (drift != 0 && weighted.Count > 0)
        {
            Add(scores, weighted[0].PlayerId, drift);
            AddEvent(
                events,
                weighted[0].PlayerId,
                "Pool rounding",
                drift,
                $"Pool rounding adjustment: {drift} point{(Math.Abs(drift) == 1 ? "" : "s")} after splitting the pool.");
        }
    }

    private static int SelfReportAmount(int windowSeconds, TimeSpan elapsed)
    {
        var total = Math.Max(1, windowSeconds);
        var ratio = Math.Clamp(elapsed.TotalSeconds / total, 0, 1);
        return (int)Math.Round(25 - ratio * 20);
    }

    public static int VotePenalty(ConfidenceLevel confidence) => confidence switch
    {
        ConfidenceLevel.Low => 0,
        ConfidenceLevel.Medium => 20,
        ConfidenceLevel.High => 60,
        _ => 0
    };

    public static int ConfidenceWeight(ConfidenceLevel confidence) => confidence switch
    {
        ConfidenceLevel.Low => 1,
        ConfidenceLevel.Medium => 2,
        ConfidenceLevel.High => 4,
        _ => 1
    };

    private static double StableJitter(Guid playerId, Guid answerId)
    {
        var bytes = playerId.ToByteArray().Concat(answerId.ToByteArray()).ToArray();
        var hash = bytes.Aggregate(17, (current, value) => current * 31 + value);
        return Math.Abs(hash % 1000) / 1000.0;
    }

    private static int Subtract(Dictionary<Guid, int> scores, Guid playerId, int amount)
    {
        var current = scores.GetValueOrDefault(playerId);
        var realized = Math.Min(current, Math.Max(0, amount));
        scores[playerId] = current - realized;
        return realized;
    }

    private static void Add(Dictionary<Guid, int> scores, Guid playerId, int amount)
    {
        scores[playerId] = scores.GetValueOrDefault(playerId) + amount;
    }

    private static string Pluralize(int count, string singular) => count == 1 ? singular : $"{singular}s";

    private static void AddEvent(List<ScoreEventDto> events, Guid playerId, string reason, int delta, string detail)
    {
        if (delta != 0)
        {
            events.Add(new ScoreEventDto(playerId, reason, delta, detail));
        }
    }
}

public sealed record ScoringInput(
    int RoundNumber,
    int Rollover,
    int SelfReportWindowSeconds,
    DateTimeOffset SelfReportOpenedAt,
    PromptRound Round,
    string MajorityPrompt,
    string AlternatePrompt,
    Guid ImposterPlayerId,
    IReadOnlyList<ScoringPlayer> Players,
    IReadOnlyList<SubmittedAnswer> Answers,
    IReadOnlyList<PlayerVote> Votes,
    IReadOnlyList<SelfReportClaim> SelfReports,
    IReadOnlyList<SafeBetClaim> SafeBets)
{
    public string PlayerName(Guid playerId) => Players.First(x => x.Id == playerId).Name;
}

public sealed record ScoringPlayer(Guid Id, string Name, int Score);

public sealed record SubmittedAnswer(Guid AnswerId, Guid PlayerId, string Text, int PromptIndex);

public sealed record PlayerVote(Guid PlayerId, Guid AnswerId, ConfidenceLevel Confidence, DateTimeOffset VotedAt);

public sealed record SelfReportClaim(Guid PlayerId, DateTimeOffset ClaimedAt);

public sealed record SafeBetClaim(Guid PlayerId, DateTimeOffset ClaimedAt);

public sealed record ScoringOutcome(IReadOnlyDictionary<Guid, int> Scores, RoundResultDto Result);
