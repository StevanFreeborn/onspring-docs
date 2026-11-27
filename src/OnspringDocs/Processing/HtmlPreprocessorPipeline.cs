using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using OnspringDocs.Scraping;

namespace OnspringDocs.Processing;

/// <summary>
/// Two-pass AngleSharp HTML preprocessing pipeline tailored for MadCap Flare Help Center content.
/// </summary>
public sealed partial class HtmlPreprocessorPipeline
{
  [GeneratedRegex(@"\s+")]
  private static partial Regex MultiSpaceRegex();

  [GeneratedRegex(@"^Topic\s+Audience\s*:\s*(.+)$", RegexOptions.IgnoreCase)]
  private static partial Regex TopicAudienceRegex();

  [GeneratedRegex(@"Help\s+Center\s+version\s+([\d.]+)", RegexOptions.IgnoreCase)]
  private static partial Regex HelpCenterVersionRegex();

  [GeneratedRegex(@"^(NOTE|IMPORTANT|TIP|WARNING|CAUTION)\s*:\s*", RegexOptions.IgnoreCase)]
  private static partial Regex CalloutPrefixRegex();

  [GeneratedRegex(@"font-family\s*:\s*[^;]*(Courier|Consolas|Monaco|monospace)", RegexOptions.IgnoreCase)]
  private static partial Regex MonospaceStyleRegex();

  private static readonly HashSet<string> HeadingTags = new(StringComparer.OrdinalIgnoreCase)
  {
    "h1", "h2", "h3", "h4", "h5", "h6",
  };

  /// <summary>
  /// Pass 1: Parses all included documents in parallel to map heading slugs and legacy MadCap named bookmarks.
  /// </summary>
  public async Task<IReadOnlyDictionary<string, DocumentAnchorIndex>> BuildCorpusAnchorIndexAsync(
    IReadOnlyList<(DocumentRoute Route, FetchedPage Page)> items,
    int maxConcurrency,
    CancellationToken cancellationToken = default)
  {
    var results = new DocumentAnchorIndex[items.Count];
    var indexedItems = items.Select((item, idx) => (item.Route, item.Page, Index: idx));

    await Parallel.ForEachAsync(
      indexedItems,
      new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken },
      async (entry, ct) =>
      {
        var context = BrowsingContext.New(Configuration.Default);
        var doc = await context.OpenAsync(req => req.Content(entry.Page.Html), ct);
        var main = doc.QuerySelector("#mc-main-content") ?? doc.Body;

        var validSlugs = new HashSet<string>(StringComparer.Ordinal);
        var bookmarkToSlug = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (main is not null)
        {
          StripCommentsAndBoilerplate(main);
          CloudflareEmailDecoder.DecodeEmailsInElement(main);
          ExpandInteractiveWidgets(main);
          NormalizeDocumentTitleHeadings(main, entry.Route, fallbackTitle: entry.Route.TocInfo.PrimaryTitle);

          var headings = ExtractHeadingInfos(main);
          foreach (var h in headings)
          {
            validSlugs.Add(h.Slug);
          }

          MapBookmarksToHeadingSlugs(main, bookmarkToSlug);
        }

        results[entry.Index] = new DocumentAnchorIndex(entry.Route, validSlugs, bookmarkToSlug);
      });

    return results.ToDictionary(x => x.Route.CanonicalSourcePath, x => x, StringComparer.OrdinalIgnoreCase);
  }

  /// <summary>
  /// Preprocesses a single document's DOM into clean semantic HTML ready for GFM conversion.
  /// </summary>
  public async Task<PreprocessedHtmlResult> PreprocessDocumentAsync(
    DocumentRoute route,
    string rawHtml,
    IReadOnlyDictionary<string, DocumentAnchorIndex> corpusIndex,
    CancellationToken cancellationToken = default)
  {
    var context = BrowsingContext.New(Configuration.Default);
    var doc = await context.OpenAsync(req => req.Content(rawHtml), cancellationToken);
    var main = doc.QuerySelector("#mc-main-content") ?? doc.Body
      ?? throw new InvalidDataException($"Document {route.CanonicalSourcePath} has no body.");

    string? helpCenterVersion = null;
    if (route.DocType == "home")
    {
      var verMatch = HelpCenterVersionRegex().Match(NormalizeWhitespace(main.TextContent));
      if (verMatch.Success)
      {
        helpCenterVersion = verMatch.Groups[1].Value.Trim();
      }
    }

    StripCommentsAndBoilerplate(main);
    CloudflareEmailDecoder.DecodeEmailsInElement(main);
    ExpandInteractiveWidgets(main);

    var audience = ExtractAndRemoveAudience(main);
    var title = NormalizeDocumentTitleHeadings(main, route, fallbackTitle: route.TocInfo.PrimaryTitle);

    ConvertMonospaceSpansToCode(main);
    ConvertCalloutsToBlockquotes(main);
    StripImagesAndHandleIframes(main, route.DocType);
    RemoveNamedBookmarkAnchors(main);

    var (linksTo, droppedInternalLinks) = RewriteLinks(main, route, corpusIndex);
    RemoveEmptyAnchorTags(main);
    RemoveEmptyStructuralContainers(main);

    var headings = ExtractHeadingInfos(main);
    var summary = ExtractSummary(main, title);

    return new PreprocessedHtmlResult(
      Title: title,
      Summary: summary,
      Audience: audience,
      Headings: headings,
      LinksTo: linksTo,
      CleanHtml: main.InnerHtml,
      HelpCenterVersion: helpCenterVersion,
      DroppedInternalLinksCount: droppedInternalLinks);
  }

  private static void StripCommentsAndBoilerplate(IElement main)
  {
    foreach (var node in main.Descendants<IComment>().ToList())
    {
      node.RemoveFromParent();
    }

    foreach (var el in main.QuerySelectorAll("script, style, noscript, .MCBreadcrumbsBox_0, .breadcrumbs, img.MCDropDown_Image_Icon, a[name^='kanchor']").ToList())
    {
      el.Remove();
    }
  }

  private static void ExpandInteractiveWidgets(IElement main)
  {
    // Expand .MCDropDown elements recursively (outer-to-inner or inner-to-outer)
    var dropdowns = main.QuerySelectorAll(".MCDropDown").ToList();
    foreach (var dropdown in dropdowns)
    {
      if (dropdown.Parent is null)
      {
        continue;
      }

      var doc = main.Owner!;
      var precedingLevel = FindPrecedingHeadingLevel(dropdown);
      var targetLevel = Math.Clamp(precedingLevel + 1, 2, 6);

      var headEl = dropdown.QuerySelector(".MCDropDownHead");
      var bodyEl = dropdown.QuerySelector(".MCDropDownBody");

      if (headEl is not null)
      {
        foreach (var img in headEl.QuerySelectorAll("img").ToList())
        {
          img.Remove();
        }

        var headingText = NormalizeWhitespace(headEl.TextContent);
        if (!string.IsNullOrEmpty(headingText))
        {
          var newHeading = doc.CreateElement($"h{targetLevel}");
          newHeading.TextContent = headingText;
          newHeading.SetAttribute("data-mc-dropdown-heading", "true");
          dropdown.Parent.InsertBefore(newHeading, dropdown);
        }
      }

      if (bodyEl is not null)
      {
        foreach (var child in bodyEl.ChildNodes.ToList())
        {
          dropdown.Parent.InsertBefore(child, dropdown);
        }
      }

      dropdown.Remove();
    }

    foreach (var ddHeading in main.QuerySelectorAll("[data-mc-dropdown-heading]").ToList())
    {
      ddHeading.RemoveAttribute("data-mc-dropdown-heading");
    }

    // Expand .MCExpanding inline widgets
    foreach (var expanding in main.QuerySelectorAll(".MCExpanding").ToList())
    {
      if (expanding.Parent is null)
      {
        continue;
      }

      foreach (var img in expanding.QuerySelectorAll("img").ToList())
      {
        img.Remove();
      }

      foreach (var hotspot in expanding.QuerySelectorAll("a.MCExpandingHotSpot, .MCExpandingHead").ToList())
      {
        UnwrapElement(hotspot);
      }

      foreach (var body in expanding.QuerySelectorAll(".MCExpandingBody").ToList())
      {
        UnwrapElement(body);
      }

      UnwrapElement(expanding);
    }

    // Unwrap .MCToggler hotspots (their target content is already in the DOM)
    foreach (var toggler in main.QuerySelectorAll("a.MCToggler").ToList())
    {
      foreach (var img in toggler.QuerySelectorAll("img").ToList())
      {
        img.Remove();
      }
      UnwrapElement(toggler);
    }
  }

  private static int FindPrecedingHeadingLevel(IElement element)
  {
    var root = element.Owner?.QuerySelector("#mc-main-content") ?? element.Owner?.Body;
    if (root is null)
    {
      return 2;
    }

    var lastLevel = 1;
    foreach (var el in root.QuerySelectorAll("*"))
    {
      if (ReferenceEquals(el, element))
      {
        break;
      }

      if (HeadingTags.Contains(el.TagName) &&
          !el.HasAttribute("data-mc-dropdown-heading") &&
          int.TryParse(el.TagName[1..], out var lvl))
      {
        lastLevel = lvl;
      }
    }

    return lastLevel;
  }

  private static IReadOnlyList<string> ExtractAndRemoveAudience(IElement main)
  {
    var audiences = new List<string>();
    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    foreach (var p in main.QuerySelectorAll("p").ToList())
    {
      var text = NormalizeWhitespace(p.TextContent);
      var match = TopicAudienceRegex().Match(text);
      if (!match.Success)
      {
        continue;
      }

      var rawAudience = NormalizeWhitespace(match.Groups[1].Value);
      if (!string.IsNullOrEmpty(rawAudience) && seen.Add(rawAudience))
      {
        audiences.Add(rawAudience);
      }

      p.Remove();
    }

    return audiences.AsReadOnly();
  }

  private static string NormalizeDocumentTitleHeadings(IElement main, DocumentRoute route, string fallbackTitle)
  {
    var doc = main.Owner!;

    // Home.htm uses an <h5> hero banner for the main page title before an internal <h1> section
    if (route.DocType == "home")
    {
      var heroH5 = main.QuerySelector("h5");
      if (heroH5 is not null)
      {
        var heroText = NormalizeWhitespace(heroH5.TextContent);
        if (!string.IsNullOrEmpty(heroText))
        {
          var h1 = doc.CreateElement("h1");
          h1.TextContent = heroText;
          heroH5.Replace(h1);
        }
      }
    }

    var h1List = main.QuerySelectorAll("h1").ToList();
    string title;
    if (h1List.Count > 0)
    {
      title = NormalizeWhitespace(h1List[0].TextContent);
      if (string.IsNullOrWhiteSpace(title))
      {
        title = NormalizeWhitespace(fallbackTitle);
        h1List[0].TextContent = title;
      }

      // Demote any duplicate H1s so every document has a single H1
      for (var i = 1; i < h1List.Count; i++)
      {
        var h2 = doc.CreateElement("h2");
        foreach (var child in h1List[i].ChildNodes.ToList())
        {
          h2.AppendChild(child);
        }
        h1List[i].Replace(h2);
      }
    }
    else
    {
      title = NormalizeWhitespace(doc.Title ?? string.Empty);
      if (string.IsNullOrWhiteSpace(title))
      {
        title = NormalizeWhitespace(fallbackTitle);
      }

      var h1 = doc.CreateElement("h1");
      h1.TextContent = title;
      main.InsertBefore(h1, main.FirstChild);
    }

    return title;
  }

  private static void ConvertMonospaceSpansToCode(IElement main)
  {
    var doc = main.Owner!;
    foreach (var span in main.QuerySelectorAll("span[style]").ToList())
    {
      var style = span.GetAttribute("style");
      if (string.IsNullOrEmpty(style) || !MonospaceStyleRegex().IsMatch(style))
      {
        continue;
      }

      if (span.Closest("pre, code") is not null)
      {
        continue;
      }

      var code = doc.CreateElement("code");
      code.TextContent = NormalizeWhitespace(span.TextContent);
      span.Replace(code);
    }
  }

  private static void ConvertCalloutsToBlockquotes(IElement main)
  {
    var doc = main.Owner!;
    foreach (var p in main.QuerySelectorAll("p").ToList())
    {
      if (p.Closest("blockquote, td, th") is not null)
      {
        continue;
      }

      var text = NormalizeWhitespace(p.TextContent);
      var match = CalloutPrefixRegex().Match(text);
      if (!match.Success)
      {
        continue;
      }

      var label = match.Groups[1].Value.ToUpperInvariant();

      // Normalize the leading inline label inside the paragraph if it's in a <b>/<span>
      if (p.FirstElementChild is IElement firstChild &&
          (firstChild.TagName.Equals("SPAN", StringComparison.OrdinalIgnoreCase) ||
           firstChild.TagName.Equals("B", StringComparison.OrdinalIgnoreCase) ||
           firstChild.TagName.Equals("STRONG", StringComparison.OrdinalIgnoreCase)))
      {
        var childText = NormalizeWhitespace(firstChild.TextContent);
        if (CalloutPrefixRegex().IsMatch(childText))
        {
          var strong = doc.CreateElement("strong");
          strong.TextContent = $"{label}:";
          firstChild.Replace(strong);
        }
      }

      var bq = doc.CreateElement("blockquote");
      p.Parent?.InsertBefore(bq, p);
      bq.AppendChild(p);
    }
  }

  private static void StripImagesAndHandleIframes(IElement main, string docType)
  {
    var doc = main.Owner!;

    foreach (var img in main.QuerySelectorAll("img").ToList())
    {
      img.Remove();
    }

    foreach (var iframe in main.QuerySelectorAll("iframe").ToList())
    {
      var src = iframe.GetAttribute("src")?.Trim();
      if (docType == "video" && !string.IsNullOrWhiteSpace(src))
      {
        var p = doc.CreateElement("p");
        var a = doc.CreateElement("a");
        a.SetAttribute("href", src);
        a.TextContent = "Watch embedded video";
        p.AppendChild(a);
        iframe.Replace(p);
      }
      else
      {
        iframe.Remove();
      }
    }
  }

  private static void MapBookmarksToHeadingSlugs(IElement main, Dictionary<string, string> bookmarkToSlug)
  {
    var tracker = new AnchorSlugTracker();
    var allElements = main.QuerySelectorAll("*").ToList();

    // First record every heading's deterministic slug
    var headingSlugs = new Dictionary<IElement, string>();
    foreach (var el in allElements)
    {
      if (HeadingTags.Contains(el.TagName))
      {
        var slug = tracker.NextSlug(el.TextContent);
        headingSlugs[el] = slug;
        bookmarkToSlug.TryAdd(slug, slug);
      }
    }

    // Now associate every named bookmark / id with its enclosing or nearest preceding heading slug
    string? lastHeadingSlug = null;
    for (var i = 0; i < allElements.Count; i++)
    {
      var el = allElements[i];
      if (headingSlugs.TryGetValue(el, out var currentHeadingSlug))
      {
        lastHeadingSlug = currentHeadingSlug;
      }

      var id = el.GetAttribute("id")?.Trim();
      var name = el.TagName.Equals("A", StringComparison.OrdinalIgnoreCase)
        ? el.GetAttribute("name")?.Trim()
        : null;

      foreach (var anchorKey in new[] { id, name })
      {
        if (string.IsNullOrWhiteSpace(anchorKey) ||
            anchorKey.StartsWith("kanchor", StringComparison.OrdinalIgnoreCase) ||
            anchorKey.Equals("mc-main-content", StringComparison.OrdinalIgnoreCase))
        {
          continue;
        }

        var targetSlug = ResolveTargetSlugForElement(el, i, allElements, headingSlugs, lastHeadingSlug);
        if (!string.IsNullOrEmpty(targetSlug))
        {
          bookmarkToSlug.TryAdd(anchorKey, targetSlug);
        }
      }
    }
  }

  private static string? ResolveTargetSlugForElement(
    IElement el,
    int index,
    List<IElement> allElements,
    Dictionary<IElement, string> headingSlugs,
    string? lastHeadingSlug)
  {
    var enclosingHeading = el.Closest("h1, h2, h3, h4, h5, h6");
    if (enclosingHeading is not null && headingSlugs.TryGetValue(enclosingHeading, out var enclosedSlug))
    {
      return enclosedSlug;
    }

    if (headingSlugs.TryGetValue(el, out var selfSlug))
    {
      return selfSlug;
    }

    // Look ahead slightly if an <a name="..."> sits immediately before a heading inside a <p> or container
    for (var j = index + 1; j < Math.Min(index + 4, allElements.Count); j++)
    {
      if (headingSlugs.TryGetValue(allElements[j], out var nextSlug))
      {
        return nextSlug;
      }
    }

    return lastHeadingSlug;
  }

  private static void RemoveNamedBookmarkAnchors(IElement main)
  {
    foreach (var anchor in main.QuerySelectorAll("a[name]").ToList())
    {
      if (string.IsNullOrWhiteSpace(anchor.GetAttribute("href")) &&
          string.IsNullOrWhiteSpace(anchor.TextContent))
      {
        anchor.Remove();
      }
      else
      {
        anchor.RemoveAttribute("name");
      }
    }
  }

  private static (IReadOnlyList<string> LinksTo, int DroppedCount) RewriteLinks(
    IElement main,
    DocumentRoute currentRoute,
    IReadOnlyDictionary<string, DocumentAnchorIndex> corpusIndex)
  {
    var linksTo = new SortedSet<string>(StringComparer.Ordinal);
    var droppedCount = 0;

    var baseHostUri = new Uri("https://onspring.local");
    var currentPageUri = new Uri(baseHostUri, currentRoute.TocInfo.RequestPath);
    var currentDir = Path.GetDirectoryName(currentRoute.RelativeMarkdownPath)?.Replace('\\', '/') ?? string.Empty;

    corpusIndex.TryGetValue(currentRoute.CanonicalSourcePath, out var currentAnchorIndex);

    foreach (var anchor in main.QuerySelectorAll("a[href]").ToList())
    {
      var rawHref = anchor.GetAttribute("href")?.Trim();
      if (string.IsNullOrEmpty(rawHref))
      {
        continue;
      }

      if (rawHref.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
      {
        UnwrapElement(anchor);
        continue;
      }

      // Same-document fragment link
      if (rawHref.StartsWith('#'))
      {
        var frag = Uri.UnescapeDataString(rawHref[1..]).Trim();
        var resolvedSlug = ResolveFragmentSlug(frag, currentAnchorIndex);
        if (resolvedSlug is not null)
        {
          anchor.SetAttribute("href", $"#{resolvedSlug}");
        }
        else
        {
          UnwrapElement(anchor);
        }
        continue;
      }

      if (rawHref.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
      {
        anchor.SetAttribute("href", rawHref);
        continue;
      }

      if (!Uri.TryCreate(currentPageUri, rawHref, out var resolvedUri))
      {
        UnwrapElement(anchor);
        continue;
      }

      // External link
      if (!string.Equals(resolvedUri.Host, baseHostUri.Host, StringComparison.OrdinalIgnoreCase))
      {
        anchor.SetAttribute("href", resolvedUri.AbsoluteUri);
        continue;
      }

      // Internal Onspring Help Center link
      var targetPath = resolvedUri.AbsolutePath;
      if (targetPath.StartsWith("/Help/Content/", StringComparison.OrdinalIgnoreCase) &&
          (targetPath.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
           targetPath.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
      {
        var (targetCanonical, _) = MadCapTocParser.NormalizeContentUrl(targetPath);
        if (corpusIndex.TryGetValue(targetCanonical, out var targetIndex))
        {
          var relativeMdPath = ComputePosixRelativePath(currentDir, targetIndex.Route.RelativeMarkdownPath);
          var rawFragment = resolvedUri.Fragment.TrimStart('#');
          if (!string.IsNullOrEmpty(rawFragment))
          {
            var decodedFrag = Uri.UnescapeDataString(rawFragment);
            var resolvedFrag = ResolveFragmentSlug(decodedFrag, targetIndex);
            anchor.SetAttribute("href", resolvedFrag is not null ? $"{relativeMdPath}#{resolvedFrag}" : relativeMdPath);
          }
          else
          {
            anchor.SetAttribute("href", relativeMdPath);
          }

          if (!string.Equals(targetIndex.Route.Id, currentRoute.Id, StringComparison.Ordinal))
          {
            linksTo.Add(targetIndex.Route.Id);
          }
        }
        else
        {
          // Target is excluded (e.g. --exclude-release-notes) or missing -> unwrap cleanly to prose
          UnwrapElement(anchor);
          droppedCount++;
        }
      }
      else
      {
        // Other relative non-doc Help asset links -> unwrap cleanly
        UnwrapElement(anchor);
      }
    }

    return (linksTo.ToList(), droppedCount);
  }

  private static string? ResolveFragmentSlug(string fragment, DocumentAnchorIndex? targetIndex)
  {
    if (string.IsNullOrWhiteSpace(fragment) || targetIndex is null)
    {
      return null;
    }

    if (targetIndex.BookmarkToSlug.TryGetValue(fragment, out var mapped))
    {
      return mapped;
    }

    var candidate = AnchorSlugger.Slugify(fragment);
    return targetIndex.ValidHeadingSlugs.Contains(candidate) ? candidate : null;
  }

  internal static string ComputePosixRelativePath(string fromDirPosix, string targetFilePosix)
  {
    var fromParts = string.IsNullOrEmpty(fromDirPosix)
      ? []
      : fromDirPosix.Split('/', StringSplitOptions.RemoveEmptyEntries);
    var toParts = targetFilePosix.Split('/', StringSplitOptions.RemoveEmptyEntries);

    var common = 0;
    while (common < fromParts.Length &&
           common < toParts.Length - 1 &&
           string.Equals(fromParts[common], toParts[common], StringComparison.Ordinal))
    {
      common++;
    }

    var upCount = fromParts.Length - common;
    var segments = new List<string>(upCount + (toParts.Length - common));
    for (var i = 0; i < upCount; i++)
    {
      segments.Add("..");
    }

    for (var i = common; i < toParts.Length; i++)
    {
      segments.Add(toParts[i]);
    }

    return string.Join("/", segments);
  }

  private static void RemoveEmptyAnchorTags(IElement main)
  {
    foreach (var anchor in main.QuerySelectorAll("a").ToList())
    {
      if (string.IsNullOrWhiteSpace(NormalizeWhitespace(anchor.TextContent)) && anchor.ChildElementCount == 0)
      {
        anchor.Remove();
      }
    }
  }

  private static void RemoveEmptyStructuralContainers(IElement main)
  {
    var changed = true;
    while (changed)
    {
      changed = false;
      foreach (var el in main.QuerySelectorAll("p, div, span, b, strong, em, i").ToList())
      {
        if (el.ChildElementCount == 0 && string.IsNullOrWhiteSpace(NormalizeWhitespace(el.TextContent)))
        {
          el.Remove();
          changed = true;
        }
      }
    }
  }

  private static IReadOnlyList<HeadingInfo> ExtractHeadingInfos(IElement main)
  {
    var tracker = new AnchorSlugTracker();
    var headings = new List<HeadingInfo>();

    foreach (var el in main.QuerySelectorAll("h1, h2, h3, h4, h5, h6"))
    {
      var text = NormalizeWhitespace(el.TextContent);
      if (string.IsNullOrWhiteSpace(text))
      {
        continue;
      }

      var level = int.Parse(el.TagName[1..]);
      var slug = tracker.NextSlug(text);
      headings.Add(new HeadingInfo(level, text, slug));
    }

    return headings.AsReadOnly();
  }

  private static string ExtractSummary(IElement main, string fallbackTitle)
  {
    foreach (var p in main.QuerySelectorAll("p"))
    {
      if (p.Closest("table, blockquote") is not null)
      {
        continue;
      }

      var text = NormalizeWhitespace(p.TextContent);
      if (string.IsNullOrWhiteSpace(text) ||
          text.StartsWith("Help Center version", StringComparison.OrdinalIgnoreCase))
      {
        continue;
      }

      return TruncateSummary(text, 200);
    }

    return TruncateSummary(fallbackTitle, 200);
  }

  internal static string TruncateSummary(string text, int maxLength)
  {
    var normalized = NormalizeWhitespace(text);
    if (normalized.Length <= maxLength)
    {
      return normalized;
    }

    // Prefer first sentence if it fits within bounds
    var sentenceEnd = normalized.IndexOf(". ", StringComparison.Ordinal);
    if (sentenceEnd is > 30 and < 190)
    {
      return normalized[..(sentenceEnd + 1)];
    }

    var cut = normalized.LastIndexOf(' ', maxLength - 3);
    if (cut < 20)
    {
      cut = maxLength - 3;
    }

    return normalized[..cut].TrimEnd(',', ';', ':', ' ') + "...";
  }

  private static void UnwrapElement(IElement element)
  {
    var parent = element.Parent;
    if (parent is null)
    {
      return;
    }

    foreach (var child in element.ChildNodes.ToList())
    {
      parent.InsertBefore(child, element);
    }

    element.Remove();
  }

  public static string NormalizeWhitespace(string input) =>
    MultiSpaceRegex().Replace(input.Replace('\u00A0', ' ').Replace('\u200B', ' ').Replace('\uFEFF', ' '), " ").Trim();
}

public sealed record PreprocessedHtmlResult(
  string Title,
  string Summary,
  IReadOnlyList<string> Audience,
  IReadOnlyList<HeadingInfo> Headings,
  IReadOnlyList<string> LinksTo,
  string CleanHtml,
  string? HelpCenterVersion,
  int DroppedInternalLinksCount);
