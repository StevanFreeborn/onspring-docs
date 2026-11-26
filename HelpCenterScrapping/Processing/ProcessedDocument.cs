using HelpCenterScrapping.Scraping;

namespace HelpCenterScrapping.Processing;

/// <summary>
/// Resolved output route and classification for a Help Center page.
/// </summary>
public sealed record DocumentRoute(
  string Id,
  string RelativeMarkdownPath,
  string CanonicalSourcePath,
  string Section,
  string DocType,
  Version? ReleaseVersion,
  TocEntry TocInfo);

/// <summary>
/// Heading metadata extracted from a preprocessed document.
/// </summary>
public sealed record HeadingInfo(int Level, string Text, string Slug);

/// <summary>
/// Per-document bookmark/slug index built in Pass 1 for cross-document anchor rewriting.
/// </summary>
public sealed record DocumentAnchorIndex(
  DocumentRoute Route,
  IReadOnlySet<string> ValidHeadingSlugs,
  IReadOnlyDictionary<string, string> BookmarkToSlug);

/// <summary>
/// Fully converted Markdown document ready for writing and indexing.
/// </summary>
public sealed record ProcessedDocument(
  DocumentRoute Route,
  string Title,
  string Summary,
  IReadOnlyList<string> Audience,
  IReadOnlyList<HeadingInfo> Headings,
  IReadOnlyList<string> LinksTo,
  string MarkdownContent,
  int WordCount,
  int CharCount,
  string Sha256,
  int DroppedInternalLinksCount);

/// <summary>
/// Result of processing the entire fetched corpus.
/// </summary>
public sealed record CorpusProcessingResult(
  string HelpCenterVersion,
  IReadOnlyList<ProcessedDocument> Documents,
  int TotalDroppedInternalLinks);
