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
  public int MaxConcurrency { get; init; } = 12;
  public bool ExcludeReleaseNotes { get; init; }
  public int? RecentReleasesCount { get; init; }
  public bool ExcludeVideos { get; init; }
  public bool IncludeNonTocLinks { get; init; } = true;
  public string? SaveRawHtmlDirectory { get; init; }
  public string? ConfigPath { get; init; }
  public int MaxRetries { get; init; } = 3;
  public bool ShowHelp { get; init; }

  public static string UsageText => """
    onspring-docs - Scrape Onspring Help Center into an agent-optimized Markdown docs folder.

    Usage:
      onspring-docs [options]

    Options:
      --output <dir>            Target output directory (default: docs)
      --config <path>           Path to an explicit JSON config file
      --concurrency <1-32>      Max parallel HTTP topic fetches (default: 12)
      --exclude-release-notes   Exclude all release notes topics from output
      --recent-releases <N>     Keep only the N newest versioned release note pages (plus overview)
      --exclude-videos          Exclude video stub pages from output
      --toc-only                Disable 1-hop discovery of non-TOC internal .htm links
      --save-raw-html <dir>     Optional directory to save raw fetched HTML for debugging
      -h, --help                Show this help message and exit

    Configuration precedence (highest to lowest):
      1. Environment variables: ONSPRING__BASEURL, ONSPRING__USERNAME, ONSPRING__PASSWORD
      2. --config <path> (if specified)
      3. ./appsettings.json (current working directory)
      4. <exe-dir>/appsettings.json
      5. <exe-dir>/../appsettings.json
    """;

  public static ScraperOptionsParseResult Parse(string[] args, IConfiguration? configurationOverride = null)
  {
    var outputDirectory = "docs";
    var maxConcurrency = 12;
    var excludeReleaseNotes = false;
    int? recentReleasesCount = null;
    var excludeVideos = false;
    var includeNonTocLinks = true;
    string? saveRawHtmlDirectory = null;
    string? configPath = null;
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
        case "--config":
          if (!TryReadValue(args, ref i, arg, out var cfgVal, out var cfgErr))
            return ScraperOptionsParseResult.Fail(cfgErr);
          configPath = cfgVal;
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
        MaxConcurrency = maxConcurrency,
        ExcludeReleaseNotes = excludeReleaseNotes,
        RecentReleasesCount = recentReleasesCount,
        ExcludeVideos = excludeVideos,
        IncludeNonTocLinks = includeNonTocLinks,
        SaveRawHtmlDirectory = saveRawHtmlDirectory,
        ConfigPath = configPath,
        ShowHelp = true,
      });
    }

    if (excludeReleaseNotes && recentReleasesCount is not null)
    {
      return ScraperOptionsParseResult.Fail("Cannot combine --exclude-release-notes with --recent-releases.");
    }

    if (configPath is not null)
    {
      var resolvedConfig = Path.GetFullPath(configPath);
      if (!File.Exists(resolvedConfig))
      {
        return ScraperOptionsParseResult.Fail($"Config file not found: '{resolvedConfig}'.");
      }
      configPath = resolvedConfig;
    }

    var configuration = configurationOverride ?? BuildDefaultConfiguration(configPath);
    var baseUrl = configuration["Onspring:BaseUrl"]?.Trim() ?? string.Empty;
    var username = configuration["Onspring:Username"]?.Trim() ?? string.Empty;
    var password = configuration["Onspring:Password"] ?? string.Empty;

    if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
    {
      return ScraperOptionsParseResult.Fail(
        "Missing required configuration values (Onspring:BaseUrl, Onspring:Username, Onspring:Password). " +
        "Provide them in appsettings.json, via --config <path>, or via ONSPRING__BASEURL, ONSPRING__USERNAME, ONSPRING__PASSWORD.");
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
      MaxConcurrency = maxConcurrency,
      ExcludeReleaseNotes = excludeReleaseNotes,
      RecentReleasesCount = recentReleasesCount,
      ExcludeVideos = excludeVideos,
      IncludeNonTocLinks = includeNonTocLinks,
      SaveRawHtmlDirectory = string.IsNullOrWhiteSpace(saveRawHtmlDirectory) ? null : saveRawHtmlDirectory,
      ConfigPath = configPath,
    });
  }

  internal static IConfiguration BuildDefaultConfiguration(string? explicitConfigPath = null)
  {
    var builder = new ConfigurationBuilder();
    var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    void AddOptionalJsonIfExists(string candidatePath)
    {
      var fullPath = Path.GetFullPath(candidatePath);
      if (File.Exists(fullPath) && seenPaths.Add(fullPath))
      {
        builder.AddJsonFile(fullPath, optional: true, reloadOnChange: false);
      }
    }

    // Lowest -> highest precedence
    var baseDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    var parentDir = Path.GetDirectoryName(baseDir);
    if (!string.IsNullOrEmpty(parentDir))
    {
      AddOptionalJsonIfExists(Path.Combine(parentDir, "appsettings.json"));
    }

    AddOptionalJsonIfExists(Path.Combine(baseDir, "appsettings.json"));
    AddOptionalJsonIfExists(Path.Combine(Directory.GetCurrentDirectory(), "appsettings.json"));

    if (!string.IsNullOrWhiteSpace(explicitConfigPath))
    {
      AddOptionalJsonIfExists(explicitConfigPath);
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
