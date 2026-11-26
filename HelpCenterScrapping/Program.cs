using System.Diagnostics;
using HelpCenterScrapping.Options;
using HelpCenterScrapping.Output;
using HelpCenterScrapping.Processing;
using HelpCenterScrapping.Scraping;
using Microsoft.Extensions.Logging;

var parseResult = ScraperOptions.Parse(args);
if (!parseResult.IsSuccess)
{
  Console.Error.WriteLine($"Error: {parseResult.ErrorMessage}");
  return 1;
}

if (parseResult.IsHelpRequested)
{
  Console.WriteLine(ScraperOptions.UsageText);
  return 0;
}

var options = parseResult.Options!;

using var loggerFactory = LoggerFactory.Create(builder =>
  builder.AddConsole().SetMinimumLevel(LogLevel.Information));

var logger = loggerFactory.CreateLogger<Program>();
var stopwatch = Stopwatch.StartNew();

try
{
  var authenticator = new OnspringAuthenticator(loggerFactory.CreateLogger<OnspringAuthenticator>());
  var cookies = await authenticator.AuthenticateAsync(options);

  var fetcher = new HelpCenterFetcher(loggerFactory.CreateLogger<HelpCenterFetcher>());
  var fetchResult = await fetcher.FetchAllAsync(cookies, options);

  if (options.SaveRawHtmlDirectory is not null)
  {
    logger.LogInformation("Saving {Count} raw HTML files to '{Dir}'...", fetchResult.Pages.Count, options.SaveRawHtmlDirectory);
    var rawFiles = fetchResult.Pages.ToDictionary(
      p => p.CanonicalSourcePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar).Replace(".htm", ".html", StringComparison.OrdinalIgnoreCase),
      p => p.Html,
      StringComparer.Ordinal);
    AtomicDocsWriter.WriteAllAtomically(
      options.SaveRawHtmlDirectory,
      rawFiles.ToDictionary(kv => kv.Key.Replace(Path.DirectorySeparatorChar, '/'), kv => kv.Value, StringComparer.Ordinal));
  }

  logger.LogInformation("Preprocessing and converting documents to agent-optimized Markdown...");
  var converter = new MarkdownDocumentConverter();
  var corpus = await converter.ConvertCorpusAsync(fetchResult.Pages, options);

  var outputFiles = new Dictionary<string, string>(StringComparer.Ordinal);
  foreach (var doc in corpus.Documents)
  {
    outputFiles[doc.Route.RelativeMarkdownPath] = doc.MarkdownContent;
  }

  outputFiles["AGENTS.md"] = IndexGenerator.GenerateAgentsMd(corpus);
  outputFiles["INDEX.md"] = IndexGenerator.GenerateIndexMd(fetchResult.Toc, corpus);
  outputFiles["llms.txt"] = IndexGenerator.GenerateLlmsTxt(corpus);
  outputFiles["manifest.json"] = ManifestGenerator.GenerateManifestJson(fetchResult.Toc, corpus, options);

  logger.LogInformation("Writing {FileCount} files atomically to '{OutputDir}'...", outputFiles.Count, options.OutputDirectory);
  AtomicDocsWriter.WriteAllAtomically(options.OutputDirectory, outputFiles);

  stopwatch.Stop();

  var sectionSummary = string.Join(", ",
    corpus.Documents
      .GroupBy(d => d.Route.Section, StringComparer.Ordinal)
      .OrderBy(g => g.Key, StringComparer.Ordinal)
      .Select(g => $"{g.Key}={g.Count()}"));

  logger.LogInformation(
    "Completed in {ElapsedMs}ms. HelpCenterVersion={Version}, TotalDocs={TotalDocs} ({Sections}), DroppedInternalLinks={DroppedLinks}.",
    stopwatch.ElapsedMilliseconds,
    corpus.HelpCenterVersion,
    corpus.Documents.Count,
    sectionSummary,
    corpus.TotalDroppedInternalLinks);

  return 0;
}
catch (Exception ex)
{
  logger.LogError(ex, "Scraping failed. Existing output at '{OutputDir}' was not modified.", options.OutputDirectory);
  return 1;
}

public partial class Program;
