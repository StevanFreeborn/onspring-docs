namespace OnspringDocs.Scraping;

/// <summary>
/// Metadata parsed from MadCap Flare's /Help/Data/HelpSystem.xml.
/// </summary>
internal sealed record HelpSystemMetadata(
  string TocRelativePath,
  string DefaultUrl,
  string BuildVersion,
  string BuildTime
);

/// <summary>
/// Represents a topic discovered in the MadCap Flare TOC tree (or via 1-hop link discovery).
/// </summary>
internal sealed record TocEntry(
  string CanonicalSourcePath,
  string RequestPath,
  string PrimaryTitle,
  int TocOrder,
  IReadOnlyList<string> TocPaths,
  bool InToc
);

/// <summary>
/// Represents a node in the hierarchical MadCap Flare TOC tree.
/// </summary>
internal sealed record TocTreeNode(
  int NodeId,
  string Title,
  string? CanonicalSourcePath,
  IReadOnlyList<TocTreeNode> Children
);

/// <summary>
/// Complete parsed MadCap Flare HelpSystem and TOC tree.
/// </summary>
internal sealed record MadCapTocResult(
  HelpSystemMetadata HelpSystem,
  IReadOnlyList<TocTreeNode> RootNodes,
  IReadOnlyDictionary<string, TocEntry> EntriesByPath
);

/// <summary>
/// Raw HTML page downloaded from the Help Center.
/// </summary>
internal sealed record FetchedPage(
  string CanonicalSourcePath,
  string RequestPath,
  string Html,
  TocEntry TocInfo
);

/// <summary>
/// Result of fetching all TOC pages and optional 1-hop discovered internal links.
/// </summary>
internal sealed record FetchResult(
  MadCapTocResult Toc,
  IReadOnlyList<FetchedPage> Pages,
  IReadOnlyList<string> DiscoveredNonTocUrls
);
