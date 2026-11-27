using System.Collections.Concurrent;
using System.Net;
using AngleSharp;
using Microsoft.Extensions.Logging;
using OnspringDocs.Options;

namespace OnspringDocs.Scraping;

/// <summary>
/// Downloads MadCap TOC topics and performs bounded 1-hop discovery of linked internal pages
/// using an authenticated <see cref="HttpClient"/> with deterministic retry/backoff.
/// </summary>
internal sealed partial class HelpCenterFetcher(ILogger<HelpCenterFetcher> logger)
{
  private static readonly TimeSpan[] RetryDelays =
  [
    TimeSpan.FromMilliseconds(250),
    TimeSpan.FromMilliseconds(750),
    TimeSpan.FromMilliseconds(2000),
  ];

  public async Task<FetchResult> FetchAllAsync(
    CookieContainer cookies,
    ScraperOptions options,
    CancellationToken cancellationToken = default
  )
  {
    using var handler = new HttpClientHandler
    {
      CookieContainer = cookies,
      AutomaticDecompression = DecompressionMethods.All,
      AllowAutoRedirect = true,
      CheckCertificateRevocationList = true,
    };

    using var httpClient = new HttpClient(handler, disposeHandler: false)
    {
      BaseAddress = new Uri(options.BaseUrl),
      Timeout = TimeSpan.FromSeconds(30),
    };

    LogFetchingToc(logger);
    var tocResult = await MadCapTocParser.ParseAsync(httpClient, cancellationToken).ConfigureAwait(false);
    LogDiscoveredTopics(
      logger,
      tocResult.EntriesByPath.Count,
      tocResult.HelpSystem.BuildVersion
    );

    var tocEntries = tocResult.EntriesByPath.Values
      .OrderBy(e => e.TocOrder)
      .ThenBy(e => e.CanonicalSourcePath, StringComparer.OrdinalIgnoreCase)
      .ToList();

    var fetchedPages = new ConcurrentDictionary<string, FetchedPage>(
      StringComparer.OrdinalIgnoreCase
    );
    var failedUrls = new ConcurrentBag<string>();

    await Parallel.ForEachAsync(
      tocEntries,
      new ParallelOptions
      {
        MaxDegreeOfParallelism = options.MaxConcurrency,
        CancellationToken = cancellationToken,
      },
      async (entry, ct) =>
      {
        var (ok, html, error) = await FetchWithRetryAsync(
          httpClient,
          entry.RequestPath,
          options.MaxRetries,
          ct
        ).ConfigureAwait(false);

        if (ok is false || html is null)
        {
          failedUrls.Add($"{entry.CanonicalSourcePath} ({error})");
          return;
        }

        fetchedPages[entry.CanonicalSourcePath] = new FetchedPage(
          entry.CanonicalSourcePath,
          entry.RequestPath,
          html,
          entry
        );
      }
    ).ConfigureAwait(false);

    if (failedUrls.IsEmpty is false)
    {
      var sortedFailures = failedUrls.OrderBy(x => x, StringComparer.Ordinal).ToList();
      throw new HttpRequestException(
        $"Failed to fetch {sortedFailures.Count} TOC topic(s):\n  " +
        string.Join("\n  ", sortedFailures.Take(20))
      );
    }

    var discoveredNonToc = new List<string>();
    if (options.IncludeNonTocLinks)
    {
      var candidateLinks = await DiscoverNonTocLinksAsync(
        fetchedPages.Values,
        tocResult.EntriesByPath,
        new Uri(options.BaseUrl),
        cancellationToken
      ).ConfigureAwait(false);

      if (candidateLinks.Count is > 0)
      {
        LogCheckingNonTocLinks(logger, candidateLinks.Count);
        var baseOrder = tocResult.EntriesByPath.Count;
        var indexedCandidates = candidateLinks
          .Select((c, index) => (c.CanonicalPath, c.RequestPath, Order: baseOrder + index))
          .ToList();

        await Parallel.ForEachAsync(
          indexedCandidates,
          new ParallelOptions
          {
            MaxDegreeOfParallelism = options.MaxConcurrency,
            CancellationToken = cancellationToken,
          },
          async (candidate, ct) =>
          {
            var (ok, html, _) = await FetchWithRetryAsync(
              httpClient,
              candidate.RequestPath,
              options.MaxRetries,
              ct
            ).ConfigureAwait(false);

            if (ok is false || html is null)
            {
              return;
            }

            var syntheticEntry = new TocEntry(
              candidate.CanonicalPath,
              candidate.RequestPath,
              Path.GetFileNameWithoutExtension(candidate.CanonicalPath),
              candidate.Order,
              [],
              InToc: false
            );

            fetchedPages[candidate.CanonicalPath] = new FetchedPage(
              candidate.CanonicalPath,
              candidate.RequestPath,
              html,
              syntheticEntry
            );
          }
        ).ConfigureAwait(false);

        discoveredNonToc = fetchedPages.Values
          .Where(p => p.TocInfo.InToc is false)
          .Select(p => p.CanonicalSourcePath)
          .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
          .ToList();

        LogIncludedNonTocLinks(logger, discoveredNonToc.Count);
      }
    }

    var sortedPages = fetchedPages.Values
      .OrderBy(p => p.CanonicalSourcePath, StringComparer.OrdinalIgnoreCase)
      .ThenBy(p => p.CanonicalSourcePath, StringComparer.Ordinal)
      .ToList();

    return new FetchResult(tocResult, sortedPages, discoveredNonToc);
  }

  private static async Task<List<(string CanonicalPath, string RequestPath)>> DiscoverNonTocLinksAsync(
    IEnumerable<FetchedPage> pages,
    IReadOnlyDictionary<string, TocEntry> tocEntries,
    Uri baseUri,
    CancellationToken cancellationToken
  )
  {
    using var context = BrowsingContext.New(Configuration.Default);
    var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    foreach (var page in pages)
    {
      cancellationToken.ThrowIfCancellationRequested();
      using var doc = await context
        .OpenAsync(req => req.Content(page.Html), cancellationToken)
        .ConfigureAwait(false);
      var main = doc.QuerySelector("#mc-main-content") ?? doc.Body;
      if (main is null)
      {
        continue;
      }

      var pageUri = new Uri(baseUri, page.RequestPath);
      foreach (var anchor in main.QuerySelectorAll("a[href]"))
      {
        var href = anchor.GetAttribute("href")?.Trim();
        if (string.IsNullOrEmpty(href) ||
            href.StartsWith('#') ||
            href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("/cdn-cgi/", StringComparison.OrdinalIgnoreCase))
        {
          continue;
        }

        if (Uri.TryCreate(pageUri, href, out var resolved) is false)
        {
          continue;
        }

        if (string.Equals(resolved.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase) is false)
        {
          continue;
        }

        var path = resolved.AbsolutePath;
        if (path.StartsWith("/Help/Content/", StringComparison.OrdinalIgnoreCase) is false ||
            path.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) is false)
        {
          continue;
        }

        var (canonical, request) = MadCapTocParser.NormalizeContentUrl(path);
        if (tocEntries.ContainsKey(canonical) is false && candidates.ContainsKey(canonical) is false)
        {
          candidates[canonical] = request;
        }
      }
    }

    return candidates
      .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
      .Select(kv => (kv.Key, kv.Value))
      .ToList();
  }

  private static async Task<(bool Ok, string? Html, string? Error)> FetchWithRetryAsync(
    HttpClient httpClient,
    string requestPath,
    int maxRetries,
    CancellationToken cancellationToken
  )
  {
    string? lastError = null;
    var requestUri = new Uri(requestPath, UriKind.Relative);

    for (var attempt = 0; attempt <= maxRetries; attempt++)
    {
      try
      {
        using var response = await httpClient
          .GetAsync(requestUri, cancellationToken)
          .ConfigureAwait(false);
        if (response.IsSuccessStatusCode)
        {
          var html = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);
          return (true, html, null);
        }

        var statusCode = (int)response.StatusCode;
        lastError = $"HTTP {statusCode}";

        if (statusCode is not 429 and < 500)
        {
          return (false, null, lastError);
        }
      }
      catch (HttpRequestException ex)
      {
        lastError = ex.Message;
      }
      catch (TaskCanceledException ex) when (cancellationToken.IsCancellationRequested is false)
      {
        lastError = ex.Message;
      }

      if (attempt < maxRetries)
      {
        var delay = attempt < RetryDelays.Length ? RetryDelays[attempt] : RetryDelays[^1];
        await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
      }
    }

    return (false, null, lastError ?? "Unknown fetch error");
  }

  [LoggerMessage(
    Level = LogLevel.Information,
    Message = "Fetching MadCap TOC metadata..."
  )]
  private static partial void LogFetchingToc(ILogger logger);

  [LoggerMessage(
    Level = LogLevel.Information,
    Message = "Discovered {Count} unique TOC topics (BuildVersion={BuildVersion})."
  )]
  private static partial void LogDiscoveredTopics(
    ILogger logger,
    int count,
    string buildVersion
  );

  [LoggerMessage(
    Level = LogLevel.Information,
    Message = "Checking {Count} internal .htm link(s) referenced outside TOC..."
  )]
  private static partial void LogCheckingNonTocLinks(ILogger logger, int count);

  [LoggerMessage(
    Level = LogLevel.Information,
    Message = "Included {Count} reachable non-TOC internal topic(s)."
  )]
  private static partial void LogIncludedNonTocLinks(ILogger logger, int count);
}
