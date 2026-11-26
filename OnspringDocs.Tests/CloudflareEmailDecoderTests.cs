using AngleSharp;
using OnspringDocs.Processing;

namespace OnspringDocs.Tests;

public sealed class CloudflareEmailDecoderTests
{
  [Theory]
  [InlineData("becdcbceced1cccafed1d0cdceccd7d0d990ddd1d3", "support@onspring.com")]
  [InlineData("cab9bfbabaa5b8be8aa5a4b9bab8a3a4ade4a9a5a7", "support@onspring.com")]
  [InlineData("", "")]
  [InlineData("abc", "")]
  [InlineData("zzzz", "")]
  public void DecodeHex_DecodesCloudflareXorPayloads(string hex, string expected)
  {
    var actual = CloudflareEmailDecoder.DecodeHex(hex);
    Assert.Equal(expected, actual);
  }

  [Fact]
  public async Task DecodeEmailsInElement_ReplacesObfuscatedSpansAndLinks()
  {
    const string html = """
      <div id="mc-main-content">
        <p>Contact <a href="/cdn-cgi/l/email-protection#becdcbceced1cccafed1d0cdceccd7d0d990ddd1d3" target="_blank"><span class="mc-variable"><span class="__cf_email__" data-cfemail="becdcbceced1cccafed1d0cdceccd7d0d990ddd1d3">[email&#160;protected]</span></span></a> for help.</p>
      </div>
      """;

    var context = BrowsingContext.New(Configuration.Default);
    var doc = await context.OpenAsync(req => req.Content(html), TestContext.Current.CancellationToken);
    var main = doc.QuerySelector("#mc-main-content")!;

    CloudflareEmailDecoder.DecodeEmailsInElement(main);

    var anchor = main.QuerySelector("a")!;
    Assert.Equal("mailto:support@onspring.com", anchor.GetAttribute("href"));
    Assert.Equal("support@onspring.com", anchor.TextContent);
    Assert.Null(main.QuerySelector(".__cf_email__"));
  }
}
