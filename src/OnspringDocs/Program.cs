using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using OnspringDocs.Options;
using OnspringDocs.Output;
using OnspringDocs.Processing;
using OnspringDocs.Scraping;

var parseResult = ScraperOptions.Parse(args);
if (parseResult.IsSuccess is false)
{
  await Console.Error.WriteLineAsync($"Error: {parseResult.ErrorMessage}").ConfigureAwait(false);
  return 1;
}

if (parseResult.IsHelpRequested)
{
  await Console.Out.WriteLineAsync(ScraperOptions.UsageText).ConfigureAwait(false);
  return 0;
}

var options = parseResult.Options!;

using var loggerFactory = LoggerFactory.Create(
  builder => builder.AddConsole().SetMinimumLevel(LogLevel.Information)
);

var logger = loggerFactory.CreateLogger("OnspringDocs");
var stopwatch = Stopwatch.StartNew();

try
{
  var authenticator = new OnspringAuthenticator(
    loggerFactory.CreateLogger<OnspringAuthenticator>()
  );
  var cookies = await authenticator.AuthenticateAsync(options).ConfigureAwait(false);

  var fetcher = new HelpCenterFetcher(
    loggerFactory.CreateLogger<HelpCenterFetcher>()
  );
  var fetchResult = await fetcher.FetchAllAsync(cookies, options).ConfigureAwait(false);

  if (options.SaveRawHtmlDirectory is not null)
  {
    ProgramLog.SavingRawHtml(
      logger,
      fetchResult.Pages.Count,
      options.SaveRawHtmlDirectory
    );
    var rawFiles = fetchResult.Pages.ToDictionary(
      p => p.CanonicalSourcePath
        .TrimStart('/')
        .Replace('/', Path.DirectorySeparatorChar)
        .Replace(".htm", ".html", StringComparison.OrdinalIgnoreCase),
      p => p.Html,
      StringComparer.Ordinal
    );
    AtomicDocsWriter.WriteAllAtomically(
      options.SaveRawHtmlDirectory,
      rawFiles.ToDictionary(
        kv => kv.Key.Replace(Path.DirectorySeparatorChar, '/'),
        kv => kv.Value,
        StringComparer.Ordinal
      )
    );
  }

  ProgramLog.ConvertingDocuments(logger);
  var corpus = await MarkdownDocumentConverter.ConvertCorpusAsync(
    fetchResult.Pages,
    options
  ).ConfigureAwait(false);

  var outputFiles = new Dictionary<string, string>(StringComparer.Ordinal);
  foreach (var doc in corpus.Documents)
  {
    outputFiles[doc.Route.RelativeMarkdownPath] = doc.MarkdownContent;
  }

  outputFiles["AGENTS.md"] = IndexGenerator.GenerateAgentsMd(corpus);
  outputFiles["INDEX.md"] = IndexGenerator.GenerateIndexMd(fetchResult.Toc, corpus);
  outputFiles["llms.txt"] = IndexGenerator.GenerateLlmsTxt(corpus);
  outputFiles["manifest.json"] = ManifestGenerator.GenerateManifestJson(
    fetchResult.Toc,
    corpus,
    options
  );

  ProgramLog.WritingOutput(logger, outputFiles.Count, options.OutputDirectory);
  AtomicDocsWriter.WriteAllAtomically(options.OutputDirectory, outputFiles);

  stopwatch.Stop();

  var sectionSummary = string.Join(
    ", ",
    corpus.Documents
      .GroupBy(d => d.Route.Section, StringComparer.Ordinal)
      .OrderBy(g => g.Key, StringComparer.Ordinal)
      .Select(g => $"{g.Key}={g.Count()}")
  );

  ProgramLog.Completed(
    logger,
    stopwatch.ElapsedMilliseconds,
    corpus.HelpCenterVersion,
    corpus.Documents.Count,
    sectionSummary,
    corpus.TotalDroppedInternalLinks
  );

  return 0;
}
#pragma warning disable CA1031 // Top-level CLI boundary logs unexpected failures and exits cleanly with code 1
catch (Exception ex)
{
  ProgramLog.ScrapingFailed(logger, ex, options.OutputDirectory);
  return 1;
}
#pragma warning restore CA1031

internal static partial class ProgramLog
{
  [LoggerMessage(
    Level = LogLevel.Information,
    Message = "Saving {Count} raw HTML files to '{Dir}'..."
  )]
  internal static partial void SavingRawHtml(ILogger logger, int count, string dir);

  [LoggerMessage(
    Level = LogLevel.Information,
    Message = "Preprocessing and converting documents to agent-optimized Markdown..."
  )]
  internal static partial void ConvertingDocuments(ILogger logger);

  [LoggerMessage(
    Level = LogLevel.Information,
    Message = "Writing {FileCount} files atomically to '{OutputDir}'..."
  )]
  internal static partial void WritingOutput(ILogger logger, int fileCount, string outputDir);

  [LoggerMessage(
    Level = LogLevel.Information,
    Message = "Completed in {ElapsedMs}ms. HelpCenterVersion={Version}, TotalDocs={TotalDocs} ({Sections}), DroppedInternalLinks={DroppedLinks}."
  )]
  internal static partial void Completed(
    ILogger logger,
    long elapsedMs,
    string version,
    int totalDocs,
    string sections,
    int droppedLinks
  );

  [LoggerMessage(
    Level = LogLevel.Error,
    Message = "Scraping failed. Existing output at '{OutputDir}' was not modified."
  )]
  internal static partial void ScrapingFailed(
    ILogger logger,
    Exception exception,
    string outputDir
  );
}
