using Microsoft.Extensions.Configuration;

namespace HelpCenterScrapping.Options;

/// <summary>
/// Immutable configuration options controlling scraping, filtering, and output generation.
/// </summary>
public sealed record ScraperOptions
{
  public required string BaseUrl { get; init; }
  public required string Username { get; init; }
  public required string Password { get; init; }
  public string OutputDirectory { get; init; } = "docs";
  public bool Headless { get; init; } = true;
  public int MaxConcurrency { get; init; } = 12;
  public bool ExcludeReleaseNotes { get; init; }
  public int? RecentReleasesCount { get; init; }
  public bool ExcludeVideos { get; init; }
  public bool IncludeNonTocLinks { get; init; } = true;
  public string? SaveRawHtmlDirectory { get; init; }
  public int MaxRetries { get; init; } = 3;
  public bool ShowHelp { get; init; }

  public static string UsageText => """
    HelpCenterScrapping - Scrape Onspring Help Center into an agent-optimized Markdown docs folder.

    Usage:
      HelpCenterScrapping [options]

    Options:
      --output <dir>            Target output directory (default: docs)
      --headed                  Launch Playwright Chromium in headed mode (default: headless)
      --concurrency <1-32>      Max parallel HTTP topic fetches (default: 12)
      --exclude-release-notes   Exclude all release notes topics from output
      --recent-releases <N>     Keep only the N newest versioned release note pages (plus overview)
      --exclude-videos          Exclude video stub pages from output
      --toc-only                Disable 1-hop discovery of non-TOC internal .htm links
      --save-raw-html <dir>     Optional directory to save raw fetched HTML for debugging
      -h, --help                Show this help message and exit

    Configuration:
      Credentials are read from appsettings.json (in working directory or app base directory)
      and can be overridden via environment variables:
        ONSPRING__BASEURL, ONSPRING__USERNAME, ONSPRING__PASSWORD
    """;

  public static ScraperOptionsParseResult Parse(string[] args, IConfiguration? configurationOverride = null)
  {
    var outputDirectory = "docs";
    var headless = true;
    var maxConcurrency = 12;
    var excludeReleaseNotes = false;
    int? recentReleasesCount = null;
    var excludeVideos = false;
    var includeNonTocLinks = true;
    string? saveRawHtmlDirectory = null;
    var showHelp = false;

    for (var i = 0; i < args.Length; i++)
    {
      var arg = args[i];
      switch (arg)
      {
        case "-h":
        case "--help":
          showHelp = true;
          break;
        case "--headed":
          headless = false;
          break;
        case "--exclude-release-notes":
          excludeReleaseNotes = true;
          break;
        case "--exclude-videos":
          excludeVideos = true;
          break;
        case "--toc-only":
          includeNonTocLinks = false;
          break;
        case "--output":
          if (!TryReadValue(args, ref i, arg, out var outputVal, out var outErr))
            return ScraperOptionsParseResult.Fail(outErr);
          outputDirectory = outputVal;
          break;
        case "--concurrency":
          if (!TryReadValue(args, ref i, arg, out var concVal, out var concErr))
            return ScraperOptionsParseResult.Fail(concErr);
          if (!int.TryParse(concVal, out maxConcurrency) || maxConcurrency is < 1 or > 32)
            return ScraperOptionsParseResult.Fail($"Invalid --concurrency value '{concVal}'. Expected an integer between 1 and 32.");
          break;
        case "--recent-releases":
          if (!TryReadValue(args, ref i, arg, out var relVal, out var relErr))
            return ScraperOptionsParseResult.Fail(relErr);
          if (!int.TryParse(relVal, out var relCount) || relCount < 1)
            return ScraperOptionsParseResult.Fail($"Invalid --recent-releases value '{relVal}'. Expected a positive integer.");
          recentReleasesCount = relCount;
          break;
        case "--save-raw-html":
          if (!TryReadValue(args, ref i, arg, out var rawVal, out var rawErr))
            return ScraperOptionsParseResult.Fail(rawErr);
          saveRawHtmlDirectory = rawVal;
          break;
        default:
          return ScraperOptionsParseResult.Fail($"Unknown argument: '{arg}'. Use --help to see supported options.");
      }
    }

    if (showHelp)
    {
      return ScraperOptionsParseResult.Help(new ScraperOptions
      {
        BaseUrl = string.Empty,
        Username = string.Empty,
        Password = string.Empty,
        OutputDirectory = outputDirectory,
        Headless = headless,
        MaxConcurrency = maxConcurrency,
        ExcludeReleaseNotes = excludeReleaseNotes,
        RecentReleasesCount = recentReleasesCount,
        ExcludeVideos = excludeVideos,
        IncludeNonTocLinks = includeNonTocLinks,
        SaveRawHtmlDirectory = saveRawHtmlDirectory,
        ShowHelp = true,
      });
    }

    if (excludeReleaseNotes && recentReleasesCount is not null)
    {
      return ScraperOptionsParseResult.Fail("Cannot combine --exclude-release-notes with --recent-releases.");
    }

    var configuration = configurationOverride ?? BuildDefaultConfiguration();
    var baseUrl = configuration["Onspring:BaseUrl"]?.Trim() ?? string.Empty;
    var username = configuration["Onspring:Username"]?.Trim() ?? string.Empty;
    var password = configuration["Onspring:Password"] ?? string.Empty;

    if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
    {
      return ScraperOptionsParseResult.Fail(
        "Missing required configuration values (Onspring:BaseUrl, Onspring:Username, Onspring:Password). " +
        "Provide them in appsettings.json or via ONSPRING__BASEURL, ONSPRING__USERNAME, ONSPRING__PASSWORD.");
    }

    if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsedUri) ||
        (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
    {
      return ScraperOptionsParseResult.Fail($"Invalid Onspring:BaseUrl '{baseUrl}'. Expected an absolute http/https URL.");
    }

    baseUrl = baseUrl.TrimEnd('/');

    if (string.IsNullOrWhiteSpace(outputDirectory))
    {
      return ScraperOptionsParseResult.Fail("--output directory cannot be empty.");
    }

    return ScraperOptionsParseResult.Success(new ScraperOptions
    {
      BaseUrl = baseUrl,
      Username = username,
      Password = password,
      OutputDirectory = outputDirectory,
      Headless = headless,
      MaxConcurrency = maxConcurrency,
      ExcludeReleaseNotes = excludeReleaseNotes,
      RecentReleasesCount = recentReleasesCount,
      ExcludeVideos = excludeVideos,
      IncludeNonTocLinks = includeNonTocLinks,
      SaveRawHtmlDirectory = string.IsNullOrWhiteSpace(saveRawHtmlDirectory) ? null : saveRawHtmlDirectory,
    });
  }

  private static IConfiguration BuildDefaultConfiguration()
  {
    var builder = new ConfigurationBuilder();

    var baseDirSettings = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
    if (File.Exists(baseDirSettings))
    {
      builder.AddJsonFile(baseDirSettings, optional: true, reloadOnChange: false);
    }

    var cwdSettings = Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json");
    if (File.Exists(cwdSettings) &&
        !string.Equals(Path.GetFullPath(cwdSettings), Path.GetFullPath(baseDirSettings), StringComparison.OrdinalIgnoreCase))
    {
      builder.AddJsonFile(cwdSettings, optional: true, reloadOnChange: false);
    }

    builder.AddEnvironmentVariables();
    return builder.Build();
  }

  private static bool TryReadValue(string[] args, ref int index, string flag, out string value, out string error)
  {
    if (index + 1 >= args.Length)
    {
      value = string.Empty;
      error = $"Option '{flag}' requires a value.";
      return false;
    }

    value = args[++index];
    error = string.Empty;
    return true;
  }
}

public sealed record ScraperOptionsParseResult
{
  public bool IsSuccess { get; init; }
  public bool IsHelpRequested { get; init; }
  public string? ErrorMessage { get; init; }
  public ScraperOptions? Options { get; init; }

  public static ScraperOptionsParseResult Success(ScraperOptions options) =>
    new() { IsSuccess = true, Options = options };

  public static ScraperOptionsParseResult Help(ScraperOptions options) =>
    new() { IsSuccess = true, IsHelpRequested = true, Options = options };

  public static ScraperOptionsParseResult Fail(string error) =>
    new() { IsSuccess = false, ErrorMessage = error };
}
