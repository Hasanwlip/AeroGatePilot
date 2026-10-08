using System.Net;
using System.Text.RegularExpressions;

namespace AeroGatePilot.Portal;

/// <summary>
/// Tiny template language used by portal pages:
/// <c>{{name}}</c> inserts an HTML-encoded value, <c>{{{name}}}</c> inserts a raw value,
/// <c>&lt;!--IF:flag--&gt; ... &lt;!--ENDIF:flag--&gt;</c> keeps a block only when the flag is set.
/// </summary>
public static partial class TemplateRenderer
{
    public static string Render(string template, IReadOnlyDictionary<string, string> values, IReadOnlySet<string> flags)
    {
        var result = template;
        string previous;
        do
        {
            previous = result;
            result = ConditionalPattern().Replace(result, m => flags.Contains(m.Groups[1].Value) ? m.Groups[2].Value : "");
        } while (result != previous);
        result = RawPattern().Replace(result, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : "");
        result = EncodedPattern().Replace(result, m => values.TryGetValue(m.Groups[1].Value, out var v) ? WebUtility.HtmlEncode(v) : "");
        return result;
    }

    [GeneratedRegex(@"<!--IF:([\w.-]+)-->(.*?)<!--ENDIF:\1-->", RegexOptions.Singleline)]
    private static partial Regex ConditionalPattern();

    [GeneratedRegex(@"\{\{\{\s*([\w.-]+)\s*\}\}\}")]
    private static partial Regex RawPattern();

    [GeneratedRegex(@"\{\{\s*([\w.-]+)\s*\}\}")]
    private static partial Regex EncodedPattern();
}
