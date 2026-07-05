using System.Globalization;
using System.Text;

namespace Sarab.Api.Services;

public static class TextTools
{
    private static readonly Dictionary<char, char> ArabicMap = new()
    {
        ['أ'] = 'ا',
        ['إ'] = 'ا',
        ['آ'] = 'ا',
        ['ى'] = 'ي',
        ['ؤ'] = 'و',
        ['ئ'] = 'ي',
        ['ة'] = 'ه'
    };

    public static string NormalizeAnswer(string value)
    {
        var trimmed = value.Trim().ToLowerInvariant();
        var decomposed = trimmed.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            if (char.IsPunctuation(ch) || char.IsSymbol(ch))
            {
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                continue;
            }

            builder.Append(ArabicMap.GetValueOrDefault(ch, ch));
        }

        var normalized = builder.ToString().Normalize(NormalizationForm.FormC);
        return StripSimpleEnglishPlural(normalized);
    }

    public static int LevenshteinDistance(string left, string right)
    {
        if (left.Length == 0)
        {
            return right.Length;
        }

        if (right.Length == 0)
        {
            return left.Length;
        }

        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var j = 0; j <= right.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    public static bool NearlyEqual(string left, string right)
    {
        var a = NormalizeAnswer(left);
        var b = NormalizeAnswer(right);

        if (a == b)
        {
            return true;
        }

        var longest = Math.Max(a.Length, b.Length);
        if (longest <= 3)
        {
            return false;
        }

        var distance = LevenshteinDistance(a, b);
        return distance <= Math.Max(1, longest / 4);
    }

    private static string StripSimpleEnglishPlural(string value)
    {
        if (value.Length <= 3 || value.Any(ch => ch > 127))
        {
            return value;
        }

        if (value.EndsWith("ies", StringComparison.Ordinal) && value.Length > 4)
        {
            return value[..^3] + "y";
        }

        if (value.EndsWith('s') && !value.EndsWith("ss", StringComparison.Ordinal))
        {
            return value[..^1];
        }

        return value;
    }
}
