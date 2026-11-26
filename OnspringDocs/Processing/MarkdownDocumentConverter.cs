using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OnspringDocs.Options;
using OnspringDocs.Scraping;
using ReverseMarkdown;

namespace OnspringDocs.Processing;

/// <summary>
/// Converts preprocessed Help Center HTML into deterministic GitHub-Flavored Markdown with YAML frontmatter.
/// </summary>
public sealed partial class MarkdownDocumentConverter
{
  [GeneratedRegex(@"\n{3,}")]
  private static partial Regex ExcessNewlinesRegex();

  [GeneratedRegex(@"[ \t]+\n")]
  private static partial Regex TrailingLineSpacesRegex();

  [GeneratedRegex(@"\s+")]
  private static partial Regex WordSplitRegex();

  private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
  private readonly HtmlPreprocessorPipeline _pipeline = new();

  private static Converter CreateConverter()
  {
    var config = new Config
    {
      GithubFlavored = true,
      Formatting =
      {
        RemoveComments = true,
        CleanupSpaces = true,
        OutputLineEnding = "\n",
        ListBulletChar = '-',
      },
      Links =
      {
        SmartHref = true,
      },
      Tables =
      {
        CellListHandling = Config.TableCellListHandlingOption.InlineText,
        WithoutHeaderRow = Config.TableWithoutHeaderRowHandlingOption.Default,
      },
      Images =
      {
        Base64Handling = Config.Base64ImageHandling.Skip,
      },
    };
    config.Preprocess.SimplifyTableCellHtml();
    return new Converter(config);
  }

  public async Task<CorpusProcessingResult> ConvertCorpusAsync(
    IReadOnlyList<FetchedPage> fetchedPages,
    ScraperOptions options,
    CancellationToken cancellationToken = default)
  {
    var allRoutes = PathMapper.MapRoutes(fetchedPages.Select(p => p.TocInfo));
    var includedRoutes = PathMapper.FilterRoutes(allRoutes, options);

    var pagesByCanonical = fetchedPages.ToDictionary(
      p => p.CanonicalSourcePath,
      p => p,
      StringComparer.OrdinalIgnoreCase);

    var includedItems = includedRoutes
      .Select(route => (Route: route, Page: pagesByCanonical[route.CanonicalSourcePath]))
      .ToList();

    var corpusIndex = await _pipeline.BuildCorpusAnchorIndexAsync(
      includedItems,
      options.MaxConcurrency,
      cancellationToken);

    var processed = new ProcessedDocument[includedItems.Count];
    var indexedItems = includedItems.Select((item, idx) => (item.Route, item.Page, Index: idx));

    using var localConverter = new ThreadLocal<Converter>(CreateConverter);

    await Parallel.ForEachAsync(
      indexedItems,
      new ParallelOptions { MaxDegreeOfParallelism = options.MaxConcurrency, CancellationToken = cancellationToken },
      async (entry, ct) =>
      {
        var pre = await _pipeline.PreprocessDocumentAsync(entry.Route, entry.Page.Html, corpusIndex, ct);
        var converter = localConverter.Value!;
        var rawMarkdown = converter.Convert(pre.CleanHtml);
        var bodyMarkdown = PostProcessMarkdown(rawMarkdown);

        var frontmatter = BuildYamlFrontmatter(entry.Route, pre);
        var fullMarkdown = $"{frontmatter}\n{bodyMarkdown}\n";

        var byteCount = Utf8NoBom.GetBytes(fullMarkdown);
        var sha256 = Convert.ToHexStringLower(SHA256.HashData(byteCount));
        var wordCount = CountWords(bodyMarkdown);

        processed[entry.Index] = new ProcessedDocument(
          Route: entry.Route,
          Title: pre.Title,
          Summary: pre.Summary,
          Audience: pre.Audience,
          Headings: pre.Headings,
          LinksTo: pre.LinksTo,
          MarkdownContent: fullMarkdown,
          WordCount: wordCount,
          CharCount: fullMarkdown.Length,
          Sha256: sha256,
          DroppedInternalLinksCount: pre.DroppedInternalLinksCount);
      });

    // Extract HelpCenterVersion deterministically from Home.htm (or fallback to BuildVersion)
    var helpCenterVersion = "unknown";
    var homeItem = includedItems.FirstOrDefault(x => x.Route.DocType == "home");
    if (homeItem.Route is not null)
    {
      var homePre = await _pipeline.PreprocessDocumentAsync(homeItem.Route, homeItem.Page.Html, corpusIndex, cancellationToken);
      if (!string.IsNullOrWhiteSpace(homePre.HelpCenterVersion))
      {
        helpCenterVersion = homePre.HelpCenterVersion;
      }
    }

    var sortedDocs = processed
      .OrderBy(d => d.Route.Id, StringComparer.Ordinal)
      .ToList();

    var totalDroppedLinks = sortedDocs.Sum(d => d.DroppedInternalLinksCount);
    return new CorpusProcessingResult(helpCenterVersion, sortedDocs, totalDroppedLinks);
  }

  internal static string PostProcessMarkdown(string markdown)
  {
    var cleaned = markdown
      .Replace('\u00A0', ' ')
      .Replace("\u200B", string.Empty)
      .Replace("\uFEFF", string.Empty)
      .Replace("\r\n", "\n")
      .Replace('\r', '\n')
      .Replace(" &gt; ", " > ", StringComparison.Ordinal);

    cleaned = TrailingLineSpacesRegex().Replace(cleaned, "\n");
    cleaned = ExcessNewlinesRegex().Replace(cleaned, "\n\n");
    return cleaned.Trim();
  }

  internal static string BuildYamlFrontmatter(DocumentRoute route, PreprocessedHtmlResult pre)
  {
    var sb = new StringBuilder();
    sb.Append("---\n");
    sb.Append("id: ").Append(FormatYamlScalar(route.Id)).Append('\n');
    sb.Append("title: ").Append(FormatYamlScalar(pre.Title)).Append('\n');
    sb.Append("section: ").Append(FormatYamlScalar(route.Section)).Append('\n');
    sb.Append("doc_type: ").Append(FormatYamlScalar(route.DocType)).Append('\n');

    if (pre.Audience.Count > 0)
    {
      sb.Append("audience:\n");
      foreach (var item in pre.Audience)
      {
        sb.Append("  - ").Append(FormatYamlScalar(item)).Append('\n');
      }
    }

    if (route.TocInfo.TocPaths.Count > 0)
    {
      sb.Append("toc_paths:\n");
      foreach (var path in route.TocInfo.TocPaths)
      {
        sb.Append("  - ").Append(FormatYamlScalar(path)).Append('\n');
      }
    }

    sb.Append("source_path: ").Append(FormatYamlScalar(route.CanonicalSourcePath)).Append('\n');

    if (!string.IsNullOrWhiteSpace(pre.Summary))
    {
      sb.Append("summary: ").Append(FormatYamlScalar(pre.Summary)).Append('\n');
    }

    sb.Append("---\n");
    return sb.ToString();
  }

  internal static string FormatYamlScalar(string value)
  {
    var trimmed = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
    if (trimmed.Length == 0)
    {
      return "\"\"";
    }

    var needsQuoting =
      trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) ||
      trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) ||
      trimmed.Equals("null", StringComparison.OrdinalIgnoreCase) ||
      trimmed.Equals("~", StringComparison.Ordinal) ||
      char.IsDigit(trimmed[0]) ||
      trimmed[0] is '-' or '?' or ':' or '[' or ']' or '{' or '}' or '#' or '&' or '*' or '!' or '|' or '>' or '\'' or '"' or '%' or '@' or '`' ||
      trimmed.Contains(": ", StringComparison.Ordinal) ||
      trimmed.Contains(" #", StringComparison.Ordinal) ||
      trimmed.EndsWith(':');

    if (!needsQuoting)
    {
      return trimmed;
    }

    var escaped = trimmed
      .Replace("\\", "\\\\", StringComparison.Ordinal)
      .Replace("\"", "\\\"", StringComparison.Ordinal);
    return $"\"{escaped}\"";
  }

  private static int CountWords(string text)
  {
    if (string.IsNullOrWhiteSpace(text))
    {
      return 0;
    }

    var count = 0;
    foreach (var token in WordSplitRegex().Split(text.Trim()))
    {
      if (!string.IsNullOrEmpty(token))
      {
        count++;
      }
    }

    return count;
  }
}
