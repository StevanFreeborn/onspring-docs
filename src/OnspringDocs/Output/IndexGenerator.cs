using System.Globalization;
using System.Text;
using OnspringDocs.Processing;
using OnspringDocs.Scraping;

namespace OnspringDocs.Output;

/// <summary>
/// Generates deterministic agent-facing navigation files: <c>AGENTS.md</c>, <c>INDEX.md</c>, and <c>llms.txt</c>.
/// </summary>
internal static class IndexGenerator
{
  private static readonly string[] KeyIds =
  [
    "home",
    "getting-started/glossary",
    "faqs/administrator-faqs",
    "faqs/end-user-faqs",
  ];

  private static readonly string[] CoreSectionOrder =
  [
    "Getting Started",
    "Administrators",
    "End Users",
    "Onspring AI",
    "Products",
    "FAQs",
  ];

  public static string GenerateAgentsMd(CorpusProcessingResult corpus)
  {
    var hasReleaseNotes = corpus.Documents.Any(d => d.Route.DocType is "release-notes");
    var hasVideos = corpus.Documents.Any(d => d.Route.DocType is "video");

    var sb = new StringBuilder();
    sb.Append("# Onspring Help Center Documentation Guide for AI Agents\n\n");
    sb.Append(CultureInfo.InvariantCulture, $"- **Help Center Version:** `{corpus.HelpCenterVersion}`\n");
    sb.Append(CultureInfo.InvariantCulture, $"- **Total Documents:** `{corpus.Documents.Count}` Markdown files\n\n");
    sb.Append("## How to Navigate This Knowledge Base\n\n");
    sb.Append("1. **Start with discovery files** before running broad greps:\n");
    sb.Append("   - `INDEX.md`: Full hierarchical table of contents with audience tags and one-line summaries.\n");
    sb.Append("   - `llms.txt`: Compact entry-point index for high-signal topic lookup.\n");
    sb.Append("   - `manifest.json`: Machine-readable catalog (`id`, `path`, `section`, `doc_type`, `audience`, `headings`, `links_to`, `sha256`).\n");
    sb.Append("2. **Every topic file has YAML frontmatter** (`id`, `title`, `section`, `doc_type`, `audience`, `toc_paths`, `source_path`, `summary`) and relative `.md` cross-links.\n");
    sb.Append("3. **Prefer current feature guides over historical changelogs:**\n");
    sb.Append("   - Core documentation lives under `getting-started/`, `administrators/`, `end-users/`, `ai/`, `products/`, and `faqs/`.\n");
    if (hasReleaseNotes)
    {
      sb.Append("   - `release-notes/` contains historical per-version changelogs; consult feature guides first unless specifically answering version-history questions.\n");
    }

    if (hasVideos)
    {
      sb.Append("   - `videos/` contains short video topic summaries and Brightcove video links.\n");
    }

    return sb.ToString();
  }

  public static string GenerateIndexMd(
    MadCapTocResult tocResult,
    CorpusProcessingResult corpus
  )
  {
    var docsByCanonical = corpus.Documents.ToDictionary(
      d => d.Route.CanonicalSourcePath,
      d => d,
      StringComparer.OrdinalIgnoreCase
    );

    var visitedIds = new HashSet<string>(StringComparer.Ordinal);
    var sb = new StringBuilder();

    sb.Append("# Onspring Help Center Index\n\n");
    sb.Append(CultureInfo.InvariantCulture, $"- **Help Center Version:** `{corpus.HelpCenterVersion}`\n");
    sb.Append(CultureInfo.InvariantCulture, $"- **Total Documents:** `{corpus.Documents.Count}`\n\n");
    sb.Append("## Core Documentation\n\n");

    foreach (var rootNode in tocResult.RootNodes)
    {
      if (IsSegregatedRootNode(rootNode, docsByCanonical))
      {
        continue;
      }

      AppendTocNode(sb, rootNode, depth: 0, docsByCanonical, visitedIds);
    }

    var unvisitedCoreDocs = corpus.Documents
      .Where(
        d => d.Route.DocType is not ("release-notes" or "video") &&
          visitedIds.Contains(d.Route.Id) is false
      )
      .OrderBy(d => d.Route.Id, StringComparer.Ordinal)
      .ToList();

    if (unvisitedCoreDocs.Count is > 0)
    {
      sb.Append("\n### Additional Linked Topics\n\n");
      foreach (var doc in unvisitedCoreDocs)
      {
        sb.Append(FormatDocBullet(doc, indentLevel: 0));
      }
    }

    var releaseNotes = corpus.Documents
      .Where(d => d.Route.DocType is "release-notes")
      .OrderBy(d => d.Route.ReleaseVersion is null ? 0 : 1)
      .ThenByDescending(d => d.Route.ReleaseVersion)
      .ThenBy(d => d.Route.Id, StringComparer.Ordinal)
      .ToList();

    if (releaseNotes.Count is > 0)
    {
      sb.Append("\n## Historical Release Notes\n\n");
      sb.Append("> Sorted newest version first. Consult current feature guides above before relying on older release notes.\n\n");
      foreach (var doc in releaseNotes)
      {
        sb.Append(FormatDocBullet(doc, indentLevel: 0));
      }
    }

    var videos = corpus.Documents
      .Where(d => d.Route.DocType is "video")
      .OrderBy(d => d.Route.TocInfo.TocOrder)
      .ThenBy(d => d.Route.Id, StringComparer.Ordinal)
      .ToList();

    if (videos.Count is > 0)
    {
      sb.Append("\n## Videos\n\n");
      foreach (var doc in videos)
      {
        sb.Append(FormatDocBullet(doc, indentLevel: 0));
      }
    }

    return sb.ToString();
  }

  public static string GenerateLlmsTxt(CorpusProcessingResult corpus)
  {
    var docsById = corpus.Documents.ToDictionary(
      d => d.Route.Id,
      d => d,
      StringComparer.Ordinal
    );
    var sb = new StringBuilder();

    sb.Append("# Onspring Help Center Documentation\n\n");
    sb.Append(
      CultureInfo.InvariantCulture,
      $"> Grounding documentation for the Onspring platform (Help Center version {corpus.HelpCenterVersion}, {corpus.Documents.Count} topics). Start with INDEX.md for the full hierarchy or manifest.json for structured metadata.\n\n"
    );

    sb.Append("## Navigation\n\n");
    sb.Append("- [AGENTS.md](AGENTS.md): Agent instructions for navigating and searching this docs folder.\n");
    sb.Append("- [INDEX.md](INDEX.md): Complete hierarchical table of contents.\n");
    sb.Append("- [manifest.json](manifest.json): Machine-readable JSON catalog with headings, links, and SHA-256 hashes.\n");

    foreach (var keyId in KeyIds)
    {
      if (docsById.TryGetValue(keyId, out var doc))
      {
        sb.Append(
          CultureInfo.InvariantCulture,
          $"- [{doc.Title}]({doc.Route.RelativeMarkdownPath}): {doc.Summary}\n"
        );
      }
    }

    var coreGroups = corpus.Documents
      .Where(d => d.Route.DocType is not ("release-notes" or "video"))
      .GroupBy(d => d.Route.Section, StringComparer.Ordinal)
      .ToDictionary(
        g => g.Key,
        g => g.OrderBy(d => d.Route.TocInfo.TocOrder)
          .ThenBy(d => d.Route.Id, StringComparer.Ordinal)
          .ToList(),
        StringComparer.Ordinal
      );

    var orderedSections = CoreSectionOrder
      .Where(coreGroups.ContainsKey)
      .Concat(
        coreGroups.Keys
          .Except(CoreSectionOrder, StringComparer.Ordinal)
          .OrderBy(k => k, StringComparer.Ordinal)
      )
      .ToList();

    foreach (var section in orderedSections)
    {
      var docs = coreGroups[section];
      if (docs.Count is 0)
      {
        continue;
      }

      sb.Append(CultureInfo.InvariantCulture, $"\n## {section}\n\n");
      foreach (var doc in docs)
      {
        sb.Append(
          CultureInfo.InvariantCulture,
          $"- [{doc.Title}]({doc.Route.RelativeMarkdownPath}): {doc.Summary}\n"
        );
      }
    }

    var releaseNotes = corpus.Documents
      .Where(d => d.Route.DocType is "release-notes")
      .OrderBy(d => d.Route.ReleaseVersion is null ? 0 : 1)
      .ThenByDescending(d => d.Route.ReleaseVersion)
      .ThenBy(d => d.Route.Id, StringComparer.Ordinal)
      .ToList();

    var videos = corpus.Documents
      .Where(d => d.Route.DocType is "video")
      .OrderBy(d => d.Route.TocInfo.TocOrder)
      .ThenBy(d => d.Route.Id, StringComparer.Ordinal)
      .ToList();

    if (releaseNotes.Count is > 0 || videos.Count is > 0)
    {
      sb.Append("\n## Optional\n\n");
      if (releaseNotes.Count is > 0)
      {
        foreach (var rn in releaseNotes.Take(6))
        {
          sb.Append(
            CultureInfo.InvariantCulture,
            $"- [{rn.Title}]({rn.Route.RelativeMarkdownPath}): {rn.Summary}\n"
          );
        }

        if (releaseNotes.Count is > 6)
        {
          sb.Append(
            CultureInfo.InvariantCulture,
            $"- [Historical Release Notes ({releaseNotes.Count} total)](INDEX.md#historical-release-notes): Additional historical release notes live under release-notes/ and INDEX.md.\n"
          );
        }
      }

      if (videos.Count is > 0)
      {
        sb.Append(
          CultureInfo.InvariantCulture,
          $"- [Video Guides ({videos.Count} topics)](INDEX.md#videos): Video summaries and Brightcove links live under videos/ and INDEX.md.\n"
        );
      }
    }

    return sb.ToString();
  }

  private static bool IsSegregatedRootNode(
    TocTreeNode node,
    Dictionary<string, ProcessedDocument> docsByCanonical
  )
  {
    if (node.CanonicalSourcePath is not null &&
        docsByCanonical.TryGetValue(node.CanonicalSourcePath, out var doc) &&
        doc.Route.DocType is "release-notes" or "video")
    {
      return true;
    }

    return node.Title.Equals("Release Notes", StringComparison.OrdinalIgnoreCase) ||
      node.Title.Equals("Videos", StringComparison.OrdinalIgnoreCase);
  }

  private static void AppendTocNode(
    StringBuilder sb,
    TocTreeNode node,
    int depth,
    Dictionary<string, ProcessedDocument> docsByCanonical,
    HashSet<string> visitedIds
  )
  {
    ProcessedDocument? doc = null;
    if (node.CanonicalSourcePath is not null)
    {
      docsByCanonical.TryGetValue(node.CanonicalSourcePath, out doc);
    }

    var hasVisibleDescendants = HasAnyIncludedDoc(node, docsByCanonical);
    if (doc is null && hasVisibleDescendants is false)
    {
      return;
    }

    var sameTargetAsChild = doc is not null && node.Children.Any(
      c => c.CanonicalSourcePath is not null &&
        string.Equals(
          c.CanonicalSourcePath,
          doc.Route.CanonicalSourcePath,
          StringComparison.OrdinalIgnoreCase
        )
    );

    if (doc is not null && sameTargetAsChild is false)
    {
      visitedIds.Add(doc.Route.Id);
      sb.Append(FormatDocBullet(doc, depth, overrideTitle: node.Title));
    }
    else
    {
      var indent = new string(' ', depth * 2);
      sb.Append(CultureInfo.InvariantCulture, $"{indent}- **{node.Title}**\n");
    }

    foreach (var child in node.Children)
    {
      AppendTocNode(sb, child, depth + 1, docsByCanonical, visitedIds);
    }
  }

  private static bool HasAnyIncludedDoc(
    TocTreeNode node,
    Dictionary<string, ProcessedDocument> docsByCanonical
  )
  {
    if (node.CanonicalSourcePath is not null && docsByCanonical.ContainsKey(node.CanonicalSourcePath))
    {
      return true;
    }

    return node.Children.Any(c => HasAnyIncludedDoc(c, docsByCanonical));
  }

  private static string FormatDocBullet(
    ProcessedDocument doc,
    int indentLevel,
    string? overrideTitle = null
  )
  {
    var indent = new string(' ', indentLevel * 2);
    var label = string.IsNullOrWhiteSpace(overrideTitle) ? doc.Title : overrideTitle;
    var audienceTag = doc.Audience.Count is > 0
      ? $" *[{string.Join(", ", doc.Audience)}]*"
      : string.Empty;
    var summaryPart = string.IsNullOrWhiteSpace(doc.Summary) is false &&
        doc.Summary.Equals(label, StringComparison.OrdinalIgnoreCase) is false
      ? $" — {doc.Summary}"
      : string.Empty;

    return $"{indent}- [{label}]({doc.Route.RelativeMarkdownPath}){audienceTag}{summaryPart}\n";
  }
}
