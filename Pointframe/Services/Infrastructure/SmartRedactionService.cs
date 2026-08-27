using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace Pointframe.Services;

internal sealed class SmartRedactionService : ISmartRedactionService
{
    private const RegexOptions CommonRegexOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private const RegexOptions CustomPatternOptions = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    private static readonly TimeSpan CustomPatternMatchTimeout = TimeSpan.FromMilliseconds(100);
    private static readonly Regex StrictIpv4Regex = new(
        @"^(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}$",
        CommonRegexOptions);

    private static readonly DetectionRule[] BuiltInRules =
    [
        new(
            SensitiveDataType.Email,
            new Regex(@"\b[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}\b", CommonRegexOptions | RegexOptions.IgnoreCase),
            AllowCompactFallback: true),
        new(
            SensitiveDataType.Phone,
            new Regex(@"\b(?:\+?\d[\d\-\s().]{6,}\d)\b", CommonRegexOptions),
            static value => HasMinimumDigits(value, 7) && !LooksLikeIpv4(value),
            AllowOcrDigitSubstitutionFallback: true),
        new(
            SensitiveDataType.UrlQueryToken,
            new Regex(@"\b(?:token|access_token|apikey|api_key|secret|password)\s*=\s*[^&\s]+", CommonRegexOptions | RegexOptions.IgnoreCase),
            AllowCompactFallback: true),
        new(
            SensitiveDataType.Ipv4,
            new Regex(@"\b(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?:\.(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)){3}\b", CommonRegexOptions),
            AllowCompactFallback: true,
            AllowOcrDigitSubstitutionFallback: true),
        new(
            SensitiveDataType.AccessKeyLike,
            new Regex(@"\b(?:AKIA[0-9A-Z]{16}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{20,})\b", CommonRegexOptions),
            AllowCompactFallback: true),
        new(
            SensitiveDataType.JwtLike,
            new Regex(@"\b[A-Za-z0-9\-_]{12,}\.[A-Za-z0-9\-_]{12,}\.[A-Za-z0-9\-_]{12,}\b", CommonRegexOptions),
            AllowCompactFallback: true),
    ];

    private readonly IOcrRegionService _ocrRegionService;
    private readonly IUserSettingsService _settingsService;

    public SmartRedactionService(
        IOcrRegionService ocrRegionService,
        IUserSettingsService settingsService)
    {
        _ocrRegionService = ocrRegionService;
        _settingsService = settingsService;
    }

    public async Task<IReadOnlyList<SmartRedactionSuggestion>> DetectAsync(
        BitmapSource bitmap,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bitmap);

        var lines = await _ocrRegionService.RecognizeLines(bitmap, cancellationToken).ConfigureAwait(false);
        if (lines.Count == 0)
        {
            return [];
        }

        var currentSettings = _settingsService.Current;
        var rules = BuildDetectionRules(
            currentSettings.SmartRedactionExcludedBuiltInTypes,
            currentSettings.CustomRedactionPatterns);
        var suggestions = new List<SmartRedactionSuggestion>();

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var indexedLine = BuildIndexedLine(line.Words);
            if (indexedLine.Text.Length == 0)
            {
                continue;
            }

            foreach (var rule in rules)
            {
                CollectSuggestionsForRule(indexedLine, rule, suggestions);
            }
        }

        return Deduplicate(suggestions);
    }

    private static IReadOnlyList<DetectionRule> BuildDetectionRules(
        IReadOnlyList<SensitiveDataType>? excludedBuiltInTypes,
        IReadOnlyList<SmartRedactionPattern>? customPatterns)
    {
        var rules = new List<DetectionRule>(BuiltInRules.Length + (customPatterns?.Count ?? 0));
        var excludedBuiltInTypeSet = excludedBuiltInTypes is null
            ? null
            : excludedBuiltInTypes.ToHashSet();
        foreach (var builtInRule in BuiltInRules)
        {
            if (excludedBuiltInTypeSet?.Contains(builtInRule.Type) == true)
            {
                continue;
            }

            rules.Add(builtInRule);
        }

        if (customPatterns is null || customPatterns.Count == 0)
        {
            return rules;
        }

        foreach (var customPattern in customPatterns.Take(SmartRedactionPattern.MaxCount))
        {
            if (!customPattern.IsEnabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(customPattern.Pattern))
            {
                continue;
            }

            if (customPattern.Pattern.Length > SmartRedactionPattern.MaxPatternLength)
            {
                continue;
            }

            var regex = TryCreateCustomRegex(customPattern.Pattern);
            if (regex is null)
            {
                continue;
            }

            rules.Add(new DetectionRule(SensitiveDataType.CustomPattern, regex, AllowCompactFallback: true));
        }

        return rules;
    }

    private static void CollectSuggestionsForRule(
        IndexedLine line,
        DetectionRule rule,
        List<SmartRedactionSuggestion> suggestions)
    {
        var suggestionCountBeforeRule = suggestions.Count;
        CollectSuggestionsForRule(line.Text, line.WordSpans, rule, suggestions);

        if (rule.AllowCompactFallback
            && suggestions.Count == suggestionCountBeforeRule
            && line.CompactText.Length > 0
            && !string.Equals(line.CompactText, line.Text, StringComparison.Ordinal))
        {
            CollectSuggestionsForRule(line.CompactText, line.CompactWordSpans, rule, suggestions);
        }

        if (rule.AllowOcrDigitSubstitutionFallback
            && suggestions.Count == suggestionCountBeforeRule
            && line.NormalizedText.Length > 0
            && !string.Equals(line.NormalizedText, line.Text, StringComparison.Ordinal))
        {
            CollectSuggestionsForRule(line.NormalizedText, line.WordSpans, rule, suggestions);
        }

        if (rule.AllowOcrDigitSubstitutionFallback
            && rule.AllowCompactFallback
            && suggestions.Count == suggestionCountBeforeRule
            && line.NormalizedCompactText.Length > 0
            && !string.Equals(line.NormalizedCompactText, line.CompactText, StringComparison.Ordinal))
        {
            CollectSuggestionsForRule(line.NormalizedCompactText, line.CompactWordSpans, rule, suggestions);
        }
    }

    private static void CollectSuggestionsForRule(
        string lineText,
        IReadOnlyList<WordSpan> spans,
        DetectionRule rule,
        List<SmartRedactionSuggestion> suggestions)
    {
        var matches = rule.Pattern.Matches(lineText);
        foreach (Match match in matches)
        {
            if (!match.Success)
            {
                continue;
            }

            if (rule.Predicate is not null && !rule.Predicate(match.Value))
            {
                continue;
            }

            var matchStart = match.Index;
            var matchEnd = match.Index + match.Length;
            var bounds = TryGetMatchBounds(spans, matchStart, matchEnd);
            if (bounds is null)
            {
                continue;
            }

            suggestions.Add(new SmartRedactionSuggestion(bounds.Value, rule.Type));
        }
    }

    private static Regex? TryCreateCustomRegex(string pattern)
    {
        try
        {
            return new Regex(pattern, CustomPatternOptions | RegexOptions.NonBacktracking, CustomPatternMatchTimeout);
        }
        catch (NotSupportedException)
        {
            // Some regex features are unsupported by the non-backtracking engine.
        }
        catch (ArgumentException)
        {
            return null;
        }

        try
        {
            return new Regex(pattern, CustomPatternOptions, CustomPatternMatchTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static IndexedLine BuildIndexedLine(IReadOnlyList<OcrTextWord> words)
    {
        var textBuilder = new StringBuilder();
        var compactTextBuilder = new StringBuilder();
        var normalizedTextBuilder = new StringBuilder();
        var normalizedCompactTextBuilder = new StringBuilder();
        var spans = new List<WordSpan>();
        var compactSpans = new List<WordSpan>();

        foreach (var word in words)
        {
            if (string.IsNullOrWhiteSpace(word.Text))
            {
                continue;
            }

            if (word.PixelBounds.Width <= 0 || word.PixelBounds.Height <= 0)
            {
                continue;
            }

            if (textBuilder.Length > 0)
            {
                textBuilder.Append(' ');
                normalizedTextBuilder.Append(' ');
            }

            var normalizedWordText = NormalizeOcrConfusableDigits(word.Text);
            var start = textBuilder.Length;
            textBuilder.Append(word.Text);
            var end = textBuilder.Length;
            spans.Add(new WordSpan(start, end, word.PixelBounds));
            normalizedTextBuilder.Append(normalizedWordText);

            var compactStart = compactTextBuilder.Length;
            compactTextBuilder.Append(word.Text);
            var compactEnd = compactTextBuilder.Length;
            compactSpans.Add(new WordSpan(compactStart, compactEnd, word.PixelBounds));
            normalizedCompactTextBuilder.Append(normalizedWordText);
        }

        return new IndexedLine(
            textBuilder.ToString(),
            spans,
            compactTextBuilder.ToString(),
            compactSpans,
            normalizedTextBuilder.ToString(),
            normalizedCompactTextBuilder.ToString());
    }

    private static Int32Rect? TryGetMatchBounds(IReadOnlyList<WordSpan> spans, int matchStart, int matchEnd)
    {
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;
        var hasBounds = false;

        foreach (var span in spans)
        {
            if (span.End <= matchStart || span.Start >= matchEnd)
            {
                continue;
            }

            left = Math.Min(left, span.Bounds.X);
            top = Math.Min(top, span.Bounds.Y);
            right = Math.Max(right, span.Bounds.X + span.Bounds.Width);
            bottom = Math.Max(bottom, span.Bounds.Y + span.Bounds.Height);
            hasBounds = true;
        }

        if (!hasBounds)
        {
            return null;
        }

        return new Int32Rect(
            left,
            top,
            Math.Max(1, right - left),
            Math.Max(1, bottom - top));
    }

    private static IReadOnlyList<SmartRedactionSuggestion> Deduplicate(IReadOnlyList<SmartRedactionSuggestion> suggestions)
    {
        if (suggestions.Count == 0)
        {
            return [];
        }

        var deduplicated = new Dictionary<(int X, int Y, int Width, int Height), SmartRedactionSuggestion>();
        foreach (var suggestion in suggestions)
        {
            var key = (
                suggestion.PixelBounds.X,
                suggestion.PixelBounds.Y,
                suggestion.PixelBounds.Width,
                suggestion.PixelBounds.Height);

            deduplicated.TryAdd(key, suggestion);
        }

        return deduplicated.Values
            .OrderBy(suggestion => suggestion.PixelBounds.Y)
            .ThenBy(suggestion => suggestion.PixelBounds.X)
            .ThenBy(suggestion => suggestion.PixelBounds.Width)
            .ThenBy(suggestion => suggestion.PixelBounds.Height)
            .ThenBy(suggestion => suggestion.Type)
            .ToArray();
    }

    private static bool HasMinimumDigits(string value, int minimumDigits)
    {
        var digitCount = 0;
        foreach (var c in value)
        {
            if (!char.IsDigit(c))
            {
                continue;
            }

            digitCount++;
            if (digitCount >= minimumDigits)
            {
                return true;
            }
        }

        return false;
    }

    private static bool LooksLikeIpv4(string value)
    {
        if (StrictIpv4Regex.IsMatch(value))
        {
            return true;
        }

        var normalized = RemoveWhitespace(value, out var removedWhitespace);
        return removedWhitespace && StrictIpv4Regex.IsMatch(normalized);
    }

    private static string NormalizeOcrConfusableDigits(string value)
    {
        if (value.Length == 0)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        var changed = false;
        foreach (var c in value)
        {
            var normalized = c switch
            {
                'O' or 'o' => '0',
                'I' or 'l' or 'L' or '|' => '1',
                'Z' or 'z' => '2',
                'S' or 's' => '5',
                'B' => '8',
                '±' => '5',
                '\'' or '’' or '‘' or '`' => '7',
                _ => c,
            };

            if (normalized != c)
            {
                changed = true;
            }

            builder.Append(normalized);
        }

        return changed ? builder.ToString() : value;
    }

    private static string RemoveWhitespace(string value, out bool removedWhitespace)
    {
        var builder = new StringBuilder(value.Length);
        removedWhitespace = false;

        foreach (var c in value)
        {
            if (char.IsWhiteSpace(c))
            {
                removedWhitespace = true;
                continue;
            }

            builder.Append(c);
        }

        return removedWhitespace ? builder.ToString() : value;
    }

    private sealed record DetectionRule(
        SensitiveDataType Type,
        Regex Pattern,
        Func<string, bool>? Predicate = null,
        bool AllowCompactFallback = false,
        bool AllowOcrDigitSubstitutionFallback = false);

    private readonly record struct IndexedLine(
        string Text,
        IReadOnlyList<WordSpan> WordSpans,
        string CompactText,
        IReadOnlyList<WordSpan> CompactWordSpans,
        string NormalizedText,
        string NormalizedCompactText);
    private readonly record struct WordSpan(int Start, int End, Int32Rect Bounds);
}
