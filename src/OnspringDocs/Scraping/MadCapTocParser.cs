using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace OnspringDocs.Scraping;

/// <summary>
/// Parses MadCap Flare HelpSystem.xml and RequireJS TOC chunk files into a deterministic hierarchy.
/// </summary>
internal static class MadCapTocParser
{
  private sealed record ChunkTopicRef(
    string? CanonicalSourcePath,
    string? RequestPath,
    string Title,
    string Bookmark
  );

  private sealed class MutableTocEntry
  {
    public required string CanonicalSourcePath { get; init; }
    public required string RequestPath { get; init; }
    public required string PrimaryTitle { get; set; }
    public required int TocOrder { get; init; }
    public List<string> TocPaths { get; } = [];
    public HashSet<string> SeenPaths { get; } = new(StringComparer.Ordinal);
  }

  public static async Task<MadCapTocResult> ParseAsync(
    HttpClient httpClient,
    CancellationToken cancellationToken = default
  )
  {
    var helpSystemXml = await httpClient
      .GetStringAsync(new Uri("/Help/Data/HelpSystem.xml", UriKind.Relative), cancellationToken)
      .ConfigureAwait(false);
    var helpSystem = ParseHelpSystemXml(helpSystemXml);

    var tocRequestPath = NormalizeHelpRelativePath(helpSystem.TocRelativePath);
    var tocJs = await httpClient
      .GetStringAsync(new Uri(tocRequestPath, UriKind.Relative), cancellationToken)
      .ConfigureAwait(false);
    var tocJson = JsObjectLiteralToJson(tocJs);

    using var tocDoc = JsonDocument.Parse(tocJson);
    var root = tocDoc.RootElement;
    var numChunks = root.GetProperty("numchunks").GetInt32();
    var prefix = root.GetProperty("prefix").GetString()
      ?? throw new InvalidDataException("Missing 'prefix' in MadCap TOC root JS.");

    var tocDir = tocRequestPath[..(tocRequestPath.LastIndexOf('/') + 1)];
    var chunkPayloads = new List<string>(numChunks);
    for (var chunkIndex = 0; chunkIndex < numChunks; chunkIndex++)
    {
      var chunkPath = $"{tocDir}{prefix}{chunkIndex}.js";
      var chunkJs = await httpClient
        .GetStringAsync(new Uri(chunkPath, UriKind.Relative), cancellationToken)
        .ConfigureAwait(false);
      chunkPayloads.Add(chunkJs);
    }

    return ParseFromPayloads(helpSystem, tocJs, chunkPayloads);
  }

  internal static HelpSystemMetadata ParseHelpSystemXml(string xmlContent)
  {
    var xdoc = XDocument.Parse(xmlContent);
    var root = xdoc.Root ?? throw new InvalidDataException("HelpSystem.xml has no root element.");

    var toc = root.Attribute("Toc")?.Value ?? "Data/Tocs/OnspringTOC.js";
    var defaultUrl = root.Attribute("DefaultUrl")?.Value ?? "Content/Home.htm";
    var buildVersion = root.Attribute("BuildVersion")?.Value ?? string.Empty;
    var buildTime = root.Attribute("BuildTime")?.Value ?? string.Empty;

    return new HelpSystemMetadata(toc, defaultUrl, buildVersion, buildTime);
  }

  internal static MadCapTocResult ParseFromPayloads(
    HelpSystemMetadata helpSystem,
    string tocRootJs,
    IReadOnlyList<string> chunkJsPayloads
  )
  {
    var byChunkAndId = new Dictionary<(int ChunkIndex, int NodeId), ChunkTopicRef>();
    var byNodeId = new Dictionary<int, ChunkTopicRef>();

    for (var chunkIndex = 0; chunkIndex < chunkJsPayloads.Count; chunkIndex++)
    {
      var chunkJson = JsObjectLiteralToJson(chunkJsPayloads[chunkIndex]);
      using var chunkDoc = JsonDocument.Parse(chunkJson);

      foreach (var prop in chunkDoc.RootElement.EnumerateObject())
      {
        var rawContentPath = prop.Name;
        var isContentTopic = IsContentTopicPath(rawContentPath);
        string? canonicalPath = null;
        string? requestPath = null;
        if (isContentTopic)
        {
          (canonicalPath, requestPath) = NormalizeContentUrl(rawContentPath);
        }

        var obj = prop.Value;
        var ids = obj.TryGetProperty("i", out var iEl) ? iEl : default;
        var titles = obj.TryGetProperty("t", out var tEl) ? tEl : default;
        var bookmarks = obj.TryGetProperty("b", out var bEl) ? bEl : default;

        if (ids.ValueKind is not JsonValueKind.Array)
        {
          continue;
        }

        var count = ids.GetArrayLength();
        for (var idx = 0; idx < count; idx++)
        {
          var nodeId = ids[idx].GetInt32();
          var title = titles.ValueKind is JsonValueKind.Array && idx < titles.GetArrayLength()
            ? titles[idx].GetString() ?? string.Empty
            : string.Empty;
          var bookmark = bookmarks.ValueKind is JsonValueKind.Array && idx < bookmarks.GetArrayLength()
            ? bookmarks[idx].GetString() ?? string.Empty
            : string.Empty;

          var topicRef = new ChunkTopicRef(
            canonicalPath,
            requestPath,
            NormalizeText(title),
            bookmark
          );
          byChunkAndId[(chunkIndex, nodeId)] = topicRef;
          byNodeId.TryAdd(nodeId, topicRef);
        }
      }
    }

    var tocJson = JsObjectLiteralToJson(tocRootJs);
    using var tocDoc = JsonDocument.Parse(tocJson);

    var entries = new Dictionary<string, MutableTocEntry>(StringComparer.OrdinalIgnoreCase);
    var nextOrder = 0;
    var rootNodes = new List<TocTreeNode>();

    if (tocDoc.RootElement.TryGetProperty("tree", out var treeEl) &&
        treeEl.TryGetProperty("n", out var rootArray) &&
        rootArray.ValueKind is JsonValueKind.Array)
    {
      foreach (var childEl in rootArray.EnumerateArray())
      {
        rootNodes.Add(
          WalkTreeNode(childEl, [], byChunkAndId, byNodeId, entries, ref nextOrder)
        );
      }
    }

    // Ensure DefaultUrl (e.g., /Help/Content/Home.htm) is always included.
    var (defaultCanonical, defaultRequest) = NormalizeContentUrl(helpSystem.DefaultUrl);
    if (entries.ContainsKey(defaultCanonical) is false)
    {
      entries[defaultCanonical] = new MutableTocEntry
      {
        CanonicalSourcePath = defaultCanonical,
        RequestPath = defaultRequest,
        PrimaryTitle = "Home",
        TocOrder = nextOrder++,
      };
      entries[defaultCanonical].TocPaths.Add("Home");
      entries[defaultCanonical].SeenPaths.Add("Home");
    }

    var frozenEntries = entries.ToDictionary(
      kv => kv.Key,
      kv => new TocEntry(
        kv.Value.CanonicalSourcePath,
        kv.Value.RequestPath,
        kv.Value.PrimaryTitle,
        kv.Value.TocOrder,
        kv.Value.TocPaths.AsReadOnly(),
        InToc: true
      ),
      StringComparer.OrdinalIgnoreCase
    );

    return new MadCapTocResult(helpSystem, rootNodes.AsReadOnly(), frozenEntries);
  }

  private static TocTreeNode WalkTreeNode(
    JsonElement nodeEl,
    List<string> parentTrail,
    Dictionary<(int ChunkIndex, int NodeId), ChunkTopicRef> byChunkAndId,
    Dictionary<int, ChunkTopicRef> byNodeId,
    Dictionary<string, MutableTocEntry> entries,
    ref int nextOrder
  )
  {
    var nodeId = nodeEl.TryGetProperty("i", out var iEl) ? iEl.GetInt32() : -1;
    int? chunkIndex = nodeEl.TryGetProperty("c", out var cEl) ? cEl.GetInt32() : null;

    ChunkTopicRef? topicRef = null;
    if (chunkIndex is int c && byChunkAndId.TryGetValue((c, nodeId), out var exactRef))
    {
      topicRef = exactRef;
    }
    else if (nodeId is >= 0 && byNodeId.TryGetValue(nodeId, out var fallbackRef))
    {
      topicRef = fallbackRef;
    }

    var title = topicRef?.Title;
    if (string.IsNullOrWhiteSpace(title) && nodeEl.TryGetProperty("t", out var tEl))
    {
      title = NormalizeText(tEl.GetString() ?? string.Empty);
    }

    title = string.IsNullOrWhiteSpace(title) ? $"Topic {nodeId}" : title;

    var trail = new List<string>(parentTrail.Count + 1);
    trail.AddRange(parentTrail);
    if (trail.Count is 0 ||
        string.Equals(trail[^1], title, StringComparison.OrdinalIgnoreCase) is false)
    {
      trail.Add(title);
    }

    string? canonicalPath = null;
    if (topicRef?.CanonicalSourcePath is not null && topicRef.RequestPath is not null)
    {
      canonicalPath = topicRef.CanonicalSourcePath;
      var breadcrumb = string.Join(" > ", trail);

      if (entries.TryGetValue(canonicalPath, out var entry) is false)
      {
        entry = new MutableTocEntry
        {
          CanonicalSourcePath = canonicalPath,
          RequestPath = topicRef.RequestPath,
          PrimaryTitle = title,
          TocOrder = nextOrder++,
        };
        entries[canonicalPath] = entry;
      }

      if (entry.SeenPaths.Add(breadcrumb))
      {
        entry.TocPaths.Add(breadcrumb);
      }
    }

    var children = new List<TocTreeNode>();
    if (nodeEl.TryGetProperty("n", out var childrenEl) &&
        childrenEl.ValueKind is JsonValueKind.Array)
    {
      foreach (var childEl in childrenEl.EnumerateArray())
      {
        children.Add(
          WalkTreeNode(childEl, trail, byChunkAndId, byNodeId, entries, ref nextOrder)
        );
      }
    }

    return new TocTreeNode(nodeId, title, canonicalPath, children.AsReadOnly());
  }

  private static bool IsContentTopicPath(string rawPath)
  {
    var trimmed = rawPath.Split('#')[0].Split('?')[0].Trim();
    return trimmed.StartsWith("/Content/", StringComparison.OrdinalIgnoreCase) &&
      (trimmed.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
        trimmed.EndsWith(".html", StringComparison.OrdinalIgnoreCase));
  }

  /// <summary>
  /// Normalizes a MadCap Flare content URL (e.g. "/Content/ReleaseNotes/5.0_New%20Features.htm#top")
  /// into a canonical decoded path ("/Help/Content/ReleaseNotes/5.0_New Features.htm")
  /// and an HTTP-ready escaped request path ("/Help/Content/ReleaseNotes/5.0_New%20Features.htm").
  /// </summary>
  public static (string CanonicalPath, string RequestPath) NormalizeContentUrl(string rawPath)
  {
    var withoutFragment = rawPath.Split('#')[0].Split('?')[0].Trim();
    if (withoutFragment.StartsWith('/') is false)
    {
      withoutFragment = "/" + withoutFragment;
    }

    if (withoutFragment.StartsWith("/Help/", StringComparison.OrdinalIgnoreCase) is false)
    {
      withoutFragment = "/Help" + withoutFragment;
    }

    var decoded = Uri.UnescapeDataString(withoutFragment);
    var escapedSegments = decoded
      .Split('/', StringSplitOptions.RemoveEmptyEntries)
      .Select(Uri.EscapeDataString);
    var requestPath = "/" + string.Join('/', escapedSegments);

    return (decoded, requestPath);
  }

  private static string NormalizeHelpRelativePath(string relativeOrAbsolutePath)
  {
    var trimmed = relativeOrAbsolutePath.Trim();
    if (trimmed.StartsWith("/Help/", StringComparison.OrdinalIgnoreCase))
    {
      return trimmed;
    }

    return "/Help/" + trimmed.TrimStart('/');
  }

  private static string NormalizeText(string value) =>
    value.Replace('\u00A0', ' ').Trim();

  /// <summary>
  /// Deterministically converts a MadCap Flare <c>define({...});</c> JS object literal into valid JSON.
  /// Handles single-quoted strings, escaped quotes, unquoted object property names, and trailing commas.
  /// </summary>
  internal static string JsObjectLiteralToJson(string definePayload)
  {
    var span = definePayload.AsSpan().Trim();
    if (span.Length is > 0 && span[0] is '\uFEFF')
    {
      span = span[1..].TrimStart();
    }

    if (span.StartsWith("define(", StringComparison.Ordinal))
    {
      span = span["define(".Length..].TrimStart();
      if (span.EndsWith(");", StringComparison.Ordinal))
      {
        span = span[..^2].TrimEnd();
      }
      else if (span.EndsWith(')'))
      {
        span = span[..^1].TrimEnd();
      }
    }

    var sb = new StringBuilder(span.Length + 64);
    var containerStack = new Stack<char>(); // '{' or '['
    var expectingKey = false;

    for (var i = 0; i < span.Length; i++)
    {
      var ch = span[i];

      if (char.IsWhiteSpace(ch))
      {
        sb.Append(ch);
        continue;
      }

      // Single-quoted or double-quoted JS string
      if (ch is '\'' or '"')
      {
        var quote = ch;
        sb.Append('"');
        i++;
        while (i < span.Length)
        {
          var c = span[i];
          if (c is '\\' && i + 1 < span.Length)
          {
            var next = span[i + 1];
            if (next is '\'')
            {
              sb.Append('\'');
              i += 2;
              continue;
            }

            if (next is '"')
            {
              sb.Append("\\\"");
              i += 2;
              continue;
            }

            if (next is 'x' && i + 3 < span.Length)
            {
              sb.Append("\\u00").Append(span[(i + 2)..(i + 4)]);
              i += 4;
              continue;
            }

            if (next is '\\' or '/' or 'b' or 'f' or 'n' or 'r' or 't' or 'u')
            {
              sb.Append('\\').Append(next);
              i += 2;
              continue;
            }

            // Redundant JS escape (e.g., \-) -> emit literal char
            sb.Append(next);
            i += 2;
            continue;
          }

          if (c == quote)
          {
            break;
          }

          if (c is '"')
          {
            sb.Append("\\\"");
          }
          else if (c is < '\x20')
          {
            sb.Append(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}");
          }
          else
          {
            sb.Append(c);
          }

          i++;
        }

        sb.Append('"');
        expectingKey = false;
        continue;
      }

      if (ch is '{')
      {
        sb.Append('{');
        containerStack.Push('{');
        expectingKey = true;
        continue;
      }

      if (ch is '[')
      {
        sb.Append('[');
        containerStack.Push('[');
        expectingKey = false;
        continue;
      }

      if (ch is '}' or ']')
      {
        sb.Append(ch);
        if (containerStack.Count is > 0)
        {
          containerStack.Pop();
        }

        expectingKey = false;
        continue;
      }

      if (ch is ',')
      {
        // Look ahead to skip trailing commas before '}' or ']'
        var lookahead = i + 1;
        while (lookahead < span.Length && char.IsWhiteSpace(span[lookahead]))
        {
          lookahead++;
        }

        if (lookahead < span.Length && span[lookahead] is '}' or ']')
        {
          continue;
        }

        sb.Append(',');
        expectingKey = containerStack.Count is > 0 && containerStack.Peek() is '{';
        continue;
      }

      if (ch is ':')
      {
        sb.Append(':');
        expectingKey = false;
        continue;
      }

      // Unquoted JS object key identifier
      if (expectingKey && (char.IsLetter(ch) || ch is '_' or '$'))
      {
        var start = i;
        while (i + 1 < span.Length &&
               (char.IsLetterOrDigit(span[i + 1]) || span[i + 1] is '_' or '$'))
        {
          i++;
        }

        sb.Append('"').Append(span[start..(i + 1)]).Append('"');
        expectingKey = false;
        continue;
      }

      sb.Append(ch);
    }

    return sb.ToString();
  }
}
