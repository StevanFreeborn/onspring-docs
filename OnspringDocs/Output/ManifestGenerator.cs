using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using OnspringDocs.Options;
using OnspringDocs.Processing;
using OnspringDocs.Scraping;

namespace OnspringDocs.Output;

/// <summary>
/// Generates a deterministic, machine-readable <c>manifest.json</c> catalog for RAG pipelines and validation
/// using source-generated <see cref="System.Text.Json"/> serialization for Native AOT compatibility.
/// </summary>
public static partial class ManifestGenerator
{
  private static readonly ManifestJsonContext JsonContext = new(new JsonSerializerOptions
  {
    WriteIndented = true,
    Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
  });

  public static string GenerateManifestJson(
    MadCapTocResult tocResult,
    CorpusProcessingResult corpus,
    ScraperOptions options)
  {
    var sectionCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
    foreach (var doc in corpus.Documents)
    {
      sectionCounts[doc.Route.Section] = sectionCounts.GetValueOrDefault(doc.Route.Section) + 1;
    }

    var manifest = new ManifestModel
    {
      SchemaVersion = 1,
      HelpCenterVersion = corpus.HelpCenterVersion,
      MadCapBuildVersion = tocResult.HelpSystem.BuildVersion,
      MadCapBuildTime = tocResult.HelpSystem.BuildTime,
      TotalDocuments = corpus.Documents.Count,
      FiltersApplied = new ManifestFilters
      {
        ExcludeReleaseNotes = options.ExcludeReleaseNotes,
        RecentReleases = options.RecentReleasesCount,
        ExcludeVideos = options.ExcludeVideos,
        IncludeNonTocLinks = options.IncludeNonTocLinks,
      },
      Sections = sectionCounts,
      Documents = corpus.Documents
        .OrderBy(d => d.Route.Id, StringComparer.Ordinal)
        .Select(d => new ManifestDocumentEntry
        {
          Id = d.Route.Id,
          Path = d.Route.RelativeMarkdownPath,
          SourcePath = d.Route.CanonicalSourcePath,
          Title = d.Title,
          Summary = d.Summary,
          Section = d.Route.Section,
          DocType = d.Route.DocType,
          Audience = d.Audience,
          TocPaths = d.Route.TocInfo.TocPaths,
          InToc = d.Route.TocInfo.InToc,
          Headings = d.Headings.Select(h => new ManifestHeadingEntry
          {
            Level = h.Level,
            Text = h.Text,
            Slug = h.Slug,
          }).ToList(),
          LinksTo = d.LinksTo,
          WordCount = d.WordCount,
          CharCount = d.CharCount,
          Sha256 = d.Sha256,
        })
        .ToList(),
    };

    var json = JsonSerializer.Serialize(manifest, JsonContext.ManifestModel);
    return json.Replace("\r\n", "\n") + "\n";
  }

  [JsonSerializable(typeof(ManifestModel))]
  internal partial class ManifestJsonContext : JsonSerializerContext;

  internal sealed class ManifestModel
  {
    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; init; }

    [JsonPropertyName("help_center_version")]
    public required string HelpCenterVersion { get; init; }

    [JsonPropertyName("madcap_build_version")]
    public required string MadCapBuildVersion { get; init; }

    [JsonPropertyName("madcap_build_time")]
    public required string MadCapBuildTime { get; init; }

    [JsonPropertyName("total_documents")]
    public int TotalDocuments { get; init; }

    [JsonPropertyName("filters_applied")]
    public required ManifestFilters FiltersApplied { get; init; }

    [JsonPropertyName("sections")]
    public required SortedDictionary<string, int> Sections { get; init; }

    [JsonPropertyName("documents")]
    public required IReadOnlyList<ManifestDocumentEntry> Documents { get; init; }
  }

  internal sealed class ManifestFilters
  {
    [JsonPropertyName("exclude_release_notes")]
    public bool ExcludeReleaseNotes { get; init; }

    [JsonPropertyName("recent_releases")]
    public int? RecentReleases { get; init; }

    [JsonPropertyName("exclude_videos")]
    public bool ExcludeVideos { get; init; }

    [JsonPropertyName("include_non_toc_links")]
    public bool IncludeNonTocLinks { get; init; }
  }

  internal sealed class ManifestDocumentEntry
  {
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("source_path")]
    public required string SourcePath { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("section")]
    public required string Section { get; init; }

    [JsonPropertyName("doc_type")]
    public required string DocType { get; init; }

    [JsonPropertyName("audience")]
    public required IReadOnlyList<string> Audience { get; init; }

    [JsonPropertyName("toc_paths")]
    public required IReadOnlyList<string> TocPaths { get; init; }

    [JsonPropertyName("in_toc")]
    public bool InToc { get; init; }

    [JsonPropertyName("headings")]
    public required IReadOnlyList<ManifestHeadingEntry> Headings { get; init; }

    [JsonPropertyName("links_to")]
    public required IReadOnlyList<string> LinksTo { get; init; }

    [JsonPropertyName("word_count")]
    public int WordCount { get; init; }

    [JsonPropertyName("char_count")]
    public int CharCount { get; init; }

    [JsonPropertyName("sha256")]
    public required string Sha256 { get; init; }
  }

  internal sealed class ManifestHeadingEntry
  {
    [JsonPropertyName("level")]
    public int Level { get; init; }

    [JsonPropertyName("text")]
    public required string Text { get; init; }

    [JsonPropertyName("slug")]
    public required string Slug { get; init; }
  }
}
