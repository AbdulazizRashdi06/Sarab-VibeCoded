using Sarab.Api.Domain;

namespace Sarab.Api.Services;

public sealed class PromptPackValidator
{
    private static readonly HashSet<string> SupportedLanguages = new(StringComparer.OrdinalIgnoreCase)
    {
        "en",
        "ar-om"
    };

    public ValidationResultDto Validate(PromptPackUpload pack)
    {
        var errors = new List<string>();

        if (pack.SchemaVersion != 1)
        {
            errors.Add("schemaVersion must be 1.");
        }

        if (string.IsNullOrWhiteSpace(pack.Name))
        {
            errors.Add("name is required.");
        }

        if (!SupportedLanguages.Contains(pack.Language))
        {
            errors.Add("language must be either 'en' or 'ar-om'.");
        }

        if (pack.Categories.Count == 0)
        {
            errors.Add("at least one category is required.");
        }

        var categoryIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var category in pack.Categories)
        {
            if (string.IsNullOrWhiteSpace(category.Id))
            {
                errors.Add("category id is required.");
            }
            else if (!categoryIds.Add(category.Id.Trim()))
            {
                errors.Add($"category id '{category.Id}' is duplicated.");
            }

            if (string.IsNullOrWhiteSpace(category.Name))
            {
                errors.Add($"category '{category.Id}' must have a name.");
            }

            if (category.Rounds.Count == 0)
            {
                errors.Add($"category '{category.Id}' must include at least one round.");
            }

            var roundIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var round in category.Rounds)
            {
                if (string.IsNullOrWhiteSpace(round.Id))
                {
                    errors.Add($"category '{category.Id}' has a round without an id.");
                }
                else if (!roundIds.Add(round.Id.Trim()))
                {
                    errors.Add($"round id '{round.Id}' is duplicated in category '{category.Id}'.");
                }

                if (round.Prompts.Count != 2)
                {
                    errors.Add($"round '{round.Id}' must have exactly two prompts.");
                }
                else
                {
                    if (round.Prompts.Any(string.IsNullOrWhiteSpace))
                    {
                        errors.Add($"round '{round.Id}' prompts cannot be empty.");
                    }

                    if (string.Equals(round.Prompts[0].Trim(), round.Prompts[1].Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"round '{round.Id}' prompts must be different.");
                    }
                }

                if (round.Closeness is < 0 or > 100)
                {
                    errors.Add($"round '{round.Id}' closeness must be between 0 and 100.");
                }

                if (round.ObviousAnswers is null)
                {
                    continue;
                }

                foreach (var key in round.ObviousAnswers.Keys)
                {
                    if (key is not ("0" or "1"))
                    {
                        errors.Add($"round '{round.Id}' obviousAnswers keys must be '0' or '1'.");
                    }
                }
            }
        }

        return new ValidationResultDto(errors.Count == 0, errors);
    }

    public static PackLanguage ParseLanguage(string language) =>
        language.Equals("ar-om", StringComparison.OrdinalIgnoreCase) ? PackLanguage.ArOm : PackLanguage.En;

    public static string FormatLanguage(PackLanguage language) => language == PackLanguage.ArOm ? "ar-om" : "en";
}
