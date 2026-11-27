using System.Globalization;
using System.Text;
using AngleSharp.Dom;

namespace OnspringDocs.Processing;

/// <summary>
/// Decodes Cloudflare email-obfuscation hex payloads (<c>data-cfemail</c> and <c>/cdn-cgi/l/email-protection#...</c>).
/// </summary>
internal static class CloudflareEmailDecoder
{
  private const string EmailProtectionPrefix = "/cdn-cgi/l/email-protection";

  /// <summary>
  /// Decodes a Cloudflare XOR-obfuscated hex string into the plain email address.
  /// Returns <see cref="string.Empty"/> if the input is invalid.
  /// </summary>
  public static string DecodeHex(string? hex)
  {
    if (string.IsNullOrWhiteSpace(hex))
    {
      return string.Empty;
    }

    var trimmed = hex.Trim();
    if (trimmed.Length is < 4 || trimmed.Length % 2 is not 0)
    {
      return string.Empty;
    }

    try
    {
      var key = byte.Parse(
        trimmed.AsSpan(0, 2),
        NumberStyles.HexNumber,
        CultureInfo.InvariantCulture
      );
      var bytes = new byte[(trimmed.Length - 2) / 2];
      for (var i = 2; i < trimmed.Length; i += 2)
      {
        var encodedByte = byte.Parse(
          trimmed.AsSpan(i, 2),
          NumberStyles.HexNumber,
          CultureInfo.InvariantCulture
        );
        bytes[(i - 2) / 2] = (byte)(encodedByte ^ key);
      }

      return Encoding.UTF8.GetString(bytes);
    }
    catch (FormatException)
    {
      return string.Empty;
    }
  }

  /// <summary>
  /// Replaces all obfuscated Cloudflare email spans and anchor links inside <paramref name="root"/>.
  /// </summary>
  public static void DecodeEmailsInElement(IElement root)
  {
    var document = root.Owner;
    if (document is null)
    {
      return;
    }

    // First decode <span class="__cf_email__" data-cfemail="...">
    foreach (var span in root.QuerySelectorAll("span.__cf_email__[data-cfemail], [data-cfemail]").ToList())
    {
      var hex = span.GetAttribute("data-cfemail");
      var email = DecodeHex(hex);
      if (string.IsNullOrEmpty(email))
      {
        continue;
      }

      var textNode = document.CreateTextNode(email);
      span.Replace(textNode);
    }

    // Next rewrite <a href="/cdn-cgi/l/email-protection#...">
    foreach (var anchor in root.QuerySelectorAll("a[href]").ToList())
    {
      var href = anchor.GetAttribute("href");
      if (string.IsNullOrWhiteSpace(href) ||
          href.Contains(EmailProtectionPrefix, StringComparison.OrdinalIgnoreCase) is false)
      {
        continue;
      }

      var hashIndex = href.IndexOf('#', StringComparison.Ordinal);
      if (hashIndex is < 0 || hashIndex + 1 >= href.Length)
      {
        continue;
      }

      var hex = href[(hashIndex + 1)..];
      var email = DecodeHex(hex);
      if (string.IsNullOrEmpty(email))
      {
        continue;
      }

      anchor.SetAttribute("href", $"mailto:{email}");

      var currentText = anchor.TextContent.Replace('\u00A0', ' ').Trim();
      if (string.IsNullOrEmpty(currentText) ||
          (currentText.Contains("email", StringComparison.OrdinalIgnoreCase) &&
           currentText.Contains("protected", StringComparison.OrdinalIgnoreCase)))
      {
        anchor.TextContent = email;
      }
    }
  }
}
