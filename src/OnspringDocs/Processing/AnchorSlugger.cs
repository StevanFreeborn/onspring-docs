using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.RegularExpressions;

namespace OnspringDocs.Processing;

/// <summary>
/// Generates deterministic GitHub-Flavored Markdown heading slugs and tracks per-document uniqueness.
/// </summary>
internal static partial class AnchorSlugger
{
  [GeneratedRegex(@"\s+")]
  private static partial Regex WhitespaceRunRegex();

  [SuppressMessage(
    "Globalization",
    "CA1308:Normalize strings to uppercase",
    Justification = "GitHub-Flavored Markdown heading slugs are lowercase."
  )]
  public static string Slugify(string headingText)
  {
    if (string.IsNullOrWhiteSpace(headingText))
    {
      return "section";
    }

    var normalized = headingText
      .Replace('\u00A0', ' ')
      .Replace('\u200B', ' ')
      .Trim()
      .ToLowerInvariant();

    var sb = new StringBuilder(normalized.Length);
    foreach (var ch in normalized)
    {
      if (char.IsLetterOrDigit(ch) || ch is '-' or '_' || char.IsWhiteSpace(ch))
      {
        sb.Append(ch);
      }
    }

    var slug = WhitespaceRunRegex().Replace(sb.ToString().Trim(), "-").Trim('-');
    return string.IsNullOrEmpty(slug) ? "section" : slug;
  }
}

/// <summary>
/// Tracks generated heading slugs within a single document so duplicate headings receive deterministic suffixes.
/// </summary>
internal sealed class AnchorSlugTracker
{
  private readonly HashSet<string> _usedSlugs = new(StringComparer.Ordinal);

  public string NextSlug(string headingText)
  {
    var baseSlug = AnchorSlugger.Slugify(headingText);
    if (_usedSlugs.Add(baseSlug))
    {
      return baseSlug;
    }

    var suffix = 1;
    while (true)
    {
      var candidate = $"{baseSlug}-{suffix}";
      if (_usedSlugs.Add(candidate))
      {
        return candidate;
      }

      suffix++;
    }
  }
}
