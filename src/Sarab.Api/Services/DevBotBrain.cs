using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Sarab.Api.Domain;

namespace Sarab.Api.Services;

public interface IDevBotBrain
{
    Task<DevBotDecision> DecideAsync(DevBotTurn turn, CancellationToken cancellationToken = default);
}

public sealed class DevBotBrain(HttpClient http, IConfiguration configuration) : IDevBotBrain
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<DevBotDecision> DecideAsync(DevBotTurn turn, CancellationToken cancellationToken = default)
    {
        var apiKey = configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        var useLlm = configuration.GetValue("Sarab:DevBots:UseLlm", true);
        if (string.IsNullOrWhiteSpace(apiKey) || !useLlm)
        {
            throw new InvalidOperationException("Dev bots require OpenAI:ApiKey or OPENAI_API_KEY. Fallback bot logic has been removed.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(BuildRequest(turn), JsonOptions), Encoding.UTF8, "application/json");

        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        var text = ExtractResponseText(json);
        var choice = JsonSerializer.Deserialize<BotChoice>(text, JsonOptions);
        return NormalizeDecision(choice);
    }

    private object BuildRequest(DevBotTurn turn)
    {
        var model = configuration["Sarab:DevBots:Model"] ?? configuration["OpenAI:Model"] ?? "gpt-4.1-mini";
        return new
        {
            model,
            instructions = DevBotInstructions,
            input = BuildPrompt(turn),
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "sarab_dev_bot_decision",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            answer = new { type = new[] { "string", "null" }, description = "One word answer during Answer phase, otherwise null." },
                            tellChoice = new { type = new[] { "string", "null" }, @enum = new object?[] { "claim", "safe", "neutral", null } },
                            voteAnswerId = new { type = new[] { "string", "null" }, description = "Answer id to vote for during Vote phase, otherwise null." },
                            confidence = new { type = "string", @enum = new[] { "Low", "Medium", "High" } }
                        },
                        required = new[] { "answer", "tellChoice", "voteAnswerId", "confidence" }
                    }
                }
            },
            max_output_tokens = 160,
            store = false
        };
    }

    private static string BuildPrompt(DevBotTurn turn)
    {
        var answers = turn.Answers.Count == 0
            ? "No answers are visible yet."
            : string.Join("\n", turn.Answers.Select(answer =>
                $"- id={answer.Id}; word={answer.Text}; mine={answer.IsMine}; claimedMirage={answer.AuthorClaimedMirage}; betSafe={answer.AuthorBetSafe}"));

        return $"""
        You are {turn.BotName}, a development bot playtesting Sarab.
        Current phase: {turn.Phase}
        Round: {turn.RoundNumber}
        Your secret prompt: {turn.Prompt}
        Active players: {turn.ActivePlayerCount}
        Current rollover: {turn.Rollover}

        Visible anonymous answers:
        {answers}

        Choose one legal action for this phase.
        """;
    }

    private static string ExtractResponseText(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("output_text", out var outputText))
        {
            return outputText.GetString() ?? "{}";
        }

        if (!root.TryGetProperty("output", out var output))
        {
            throw new JsonException("OpenAI response did not include output text.");
        }

        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content))
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var text))
                {
                    return text.GetString() ?? "{}";
                }
            }
        }

        throw new JsonException("OpenAI response did not include text content.");
    }

    private static DevBotDecision NormalizeDecision(BotChoice? choice)
    {
        if (choice is null)
        {
            throw new JsonException("OpenAI returned an empty bot choice.");
        }

        var confidence = Enum.TryParse<ConfidenceLevel>(choice.Confidence, true, out var parsedConfidence)
            ? parsedConfidence
            : ConfidenceLevel.Medium;
        var voteAnswerId = Guid.TryParse(choice.VoteAnswerId, out var parsedVoteAnswerId) ? (Guid?)parsedVoteAnswerId : null;
        return new DevBotDecision(choice.Answer, choice.TellChoice, voteAnswerId, confidence);
    }

    private sealed record BotChoice(string? Answer, string? TellChoice, string? VoteAnswerId, string? Confidence);

    private const string DevBotInstructions = """
        You are a Sarab development bot. Sarab is a family-friendly realtime party game.
        The rules:
        - Each round, almost everyone receives the same secret prompt.
        - One player may receive a hidden alternate prompt, but no player is told whether they are the mirage.
        - In the Answer phase, submit exactly one word related to your own prompt. Avoid spaces and avoid being too obvious.
        - In the self-report/tell phase, choose:
          claim = you think you might be the mirage; correct claims earn points, false claims lose points.
          safe = you bet you are not the mirage; correct safe bets earn a small bonus, wrong safe bets lose points.
          neutral = no risk and no reward.
        - In the Vote phase, vote for one answer that is not yours. You are trying to catch the answer from the alternate prompt.
        - Confidence controls risk/reward: Low is safer, Medium is balanced, High is risky.
        Act like a plausible casual player. Return only JSON matching the schema.
        """;
}
