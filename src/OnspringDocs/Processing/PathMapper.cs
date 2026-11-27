using System.Text.RegularExpressions;
using OnspringDocs.Options;
using OnspringDocs.Scraping;

namespace OnspringDocs.Processing;

/// <summary>
/// Maps canonical Help Center source URLs to normalized kebab-case Markdown routes, sections,
/// document types, and release versions, and applies deterministic CLI filtering.
/// </summary>
public static partial class PathMapper
{
  [GeneratedRegex(@"([a-z0-9])([A-Z])")]
  private static partial Regex LowerToUpperBoundaryRegex();

  [GeneratedRegex(@"([A-Z]+)([A-Z][a-z]{2,})")]
  private static partial Regex AcronymToWordBoundaryRegex();

  [GeneratedRegex(@"^(\d+\.\d+(?:\.\d+)?)")]
  private static partial Regex ReleaseVersionPrefixRegex();

  [GeneratedRegex(@"-{2,}")]
  private static partial Regex MultiHyphenRegex();

  private static readonly Dictionary<string, string> KnownAcronymCompounds = new(StringComparer.OrdinalIgnoreCase)
  {
    ["RESTAPI"] = "rest-api",
    ["RestApi"] = "rest-api",
  };

  public static IReadOnlyList<DocumentRoute> MapRoutes(IEnumerable<TocEntry> entries)
  {
    var sortedEntries = entries
      .OrderBy(e => e.CanonicalSourcePath, StringComparer.OrdinalIgnoreCase)
      .ThenBy(e => e.CanonicalSourcePath, StringComparer.Ordinal)
      .ToList();

    var usedIds = new HashSet<string>(StringComparer.Ordinal);
    var routes = new List<DocumentRoute>(sortedEntries.Count);

    foreach (var entry in sortedEntries)
    {
      var relativeSource = StripContentPrefixAndExtension(entry.CanonicalSourcePath);
      var segments = relativeSource
        .Split('/', StringSplitOptions.RemoveEmptyEntries)
        .Select(ToKebabCase)
        .Where(s => !string.IsNullOrEmpty(s))
        .ToArray();

      var baseId = segments.Length > 0 ? string.Join("/", segments) : "home";
      var id = baseId;
      var suffix = 2;
      while (!usedIds.Add(id))
      {
        id = $"{baseId}-{suffix++}";
      }

      var (section, docType, releaseVersion) = ClassifyRoute(segments, id);

      routes.Add(new DocumentRoute(
        Id: id,
        RelativeMarkdownPath: $"{id}.md",
        CanonicalSourcePath: entry.CanonicalSourcePath,
        Section: section,
        DocType: docType,
        ReleaseVersion: releaseVersion,
        TocInfo: entry));
    }

    return routes
      .OrderBy(r => r.Id, StringComparer.Ordinal)
      .ToList();
  }

  public static IReadOnlyList<DocumentRoute> FilterRoutes(IReadOnlyList<DocumentRoute> routes, ScraperOptions options)
  {
    IEnumerable<DocumentRoute> filtered = routes;

    if (options.ExcludeVideos)
    {
      filtered = filtered.Where(r => r.DocType != "video");
    }

    if (options.ExcludeReleaseNotes)
    {
      filtered = filtered.Where(r => r.DocType != "release-notes");
    }
    else if (options.RecentReleasesCount is int keepCount)
    {
      var releaseNotes = filtered.Where(r => r.DocType == "release-notes").ToList();
      var keepReleaseIds = new HashSet<string>(StringComparer.Ordinal);

      foreach (var overview in releaseNotes.Where(r => r.ReleaseVersion is null))
      {
        keepReleaseIds.Add(overview.Id);
      }

      foreach (var versioned in releaseNotes
        .Where(r => r.ReleaseVersion is not null)
        .OrderByDescending(r => r.ReleaseVersion)
        .ThenBy(r => r.Id, StringComparer.Ordinal)
        .Take(keepCount))
      {
        keepReleaseIds.Add(versioned.Id);
      }

      filtered = filtered.Where(r => r.DocType != "release-notes" || keepReleaseIds.Contains(r.Id));
    }

    return filtered
      .OrderBy(r => r.Id, StringComparer.Ordinal)
      .ToList();
  }

  internal static string ToKebabCase(string segment)
  {
    var decoded = Uri.UnescapeDataString(segment).Trim();
    if (string.IsNullOrEmpty(decoded))
    {
      return string.Empty;
    }

    // Split on spaces/underscores first so compound tokens like RESTAPI can be normalized per token.
    var rawParts = decoded.Split([' ', '_'], StringSplitOptions.RemoveEmptyEntries);
    var normalizedParts = new List<string>(rawParts.Length);

    foreach (var part in rawParts)
    {
      var token = part;
      foreach (var kv in KnownAcronymCompounds)
      {
        token = token.Replace(kv.Key, $"-{kv.Value}-", StringComparison.OrdinalIgnoreCase);
      }

      token = AcronymToWordBoundaryRegex().Replace(token, "$1-$2");
      token = LowerToUpperBoundaryRegex().Replace(token, "$1-$2");
      token = token.ToLowerInvariant();

      // Replace invalid filename chars while preserving version dots (e.g. 5.0, 37.2)
      var chars = token.Select(c => char.IsLetterOrDigit(c) || c is '-' or '.' ? c : '-').ToArray();
      var cleaned = MultiHyphenRegex().Replace(new string(chars), "-").Trim('-', '.');
      if (!string.IsNullOrEmpty(cleaned))
      {
        normalizedParts.Add(cleaned);
      }
    }

    var combined = string.Join("-", normalizedParts);
    return MultiHyphenRegex().Replace(combined, "-").Trim('-');
  }

  private static string StripContentPrefixAndExtension(string canonicalSourcePath)
  {
    var path = canonicalSourcePath.TrimStart('/');
    if (path.StartsWith("Help/Content/", StringComparison.OrdinalIgnoreCase))
    {
      path = path["Help/Content/".Length..];
    }
    else if (path.StartsWith("Content/", StringComparison.OrdinalIgnoreCase))
    {
      path = path["Content/".Length..];
    }

    if (path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
    {
      path = path[..^4];
    }
    else if (path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
    {
      path = path[..^5];
    }

    return path;
  }

  private static (string Section, string DocType, Version? ReleaseVersion) ClassifyRoute(string[] segments, string id)
  {
    if (segments.Length == 0 || id == "home")
    {
      return ("Home", "home", null);
    }

    var top = segments[0];
    var section = top switch
    {
      "administrators" => "Administrators",
      "end-users" => "End Users",
      "ai" => "Onspring AI",
      "products" => "Products",
      "getting-started" => "Getting Started",
      "faqs" => "FAQs",
      "release-notes" => "Release Notes",
      "videos" => "Videos",
      _ => ToTitleLabel(top),
    };

    var docType = top switch
    {
      "release-notes" => "release-notes",
      "videos" => "video",
      "faqs" => "faq",
      _ when id == "getting-started/glossary" => "glossary",
      _ => "guide",
    };

    Version? releaseVersion = null;
    if (docType == "release-notes" && segments.Length >= 2)
    {
      var fileName = segments[^1];
      var match = ReleaseVersionPrefixRegex().Match(fileName);
      if (match.Success && Version.TryParse(match.Groups[1].Value, out var parsed))
      {
        releaseVersion = parsed;
      }
    }

    return (section, docType, releaseVersion);
  }

  private static string ToTitleLabel(string kebab) =>
    string.Join(" ", kebab.Split('-', StringSplitOptions.RemoveEmptyEntries)
      .Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
}
