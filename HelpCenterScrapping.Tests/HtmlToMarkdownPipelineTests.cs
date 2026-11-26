using HelpCenterScrapping.Options;
using HelpCenterScrapping.Processing;
using HelpCenterScrapping.Scraping;

namespace HelpCenterScrapping.Tests;

public sealed class HtmlToMarkdownPipelineTests
{
  [Fact]
  public async Task ConvertCorpusAsync_ExpandsFaqsDecodesEmailsRewritesLinksAndFormatsTables()
  {
    const string faqHtml = """
      <html><body><div id="mc-main-content">
        <h1>Administrator FAQs</h1>
        <p><b>Topic Audience:</b>&#160;Administrators</p>
        <h2>Access FAQs</h2>
        <div class="MCDropDown MCDropDown_Closed dropDown">
          <span class="MCDropDownHead dropDownHead">
            <a href="#" class="MCDropDownHotSpot"><img class="MCDropDown_Image_Icon" src="../../Skins/transparent.gif" />How do I get support?</a>
          </span>
          <div class="MCDropDownBody dropDownBody">
            <p>Email <a href="/cdn-cgi/l/email-protection#becdcbceced1cccafed1d0cdceccd7d0d990ddd1d3"><span class="__cf_email__" data-cfemail="becdcbceced1cccafed1d0cdceccd7d0d990ddd1d3">[email&#160;protected]</span></a> or see <a href="../Administrators/Apps/TriggersOutcomes/RESTAPI/ConfigureRESTAPIOutcome.htm#Define">Define General Settings</a>.</p>
          </div>
        </div>
      </div></body></html>
      """;

    const string restApiHtml = """
      <html><body><div id="mc-main-content">
        <h1><a name="kanchor22"></a>Configure a REST API Outcome</h1>
        <p><b>Topic Audience: </b>Technical Administrators</p>
        <p>Syntax: <span style="font-family: 'Courier New';">DateDiff(end_date, start_date, format)</span></p>
        <p><b style="color: #e8412c;">IMPORTANT:</b> Test carefully before enabling.</p>
        <p style="text-align: center;"><img src="../../../../Resources/Images/screenshot.jpg" /></p>
        <table>
          <thead><tr><th>Arg</th><th>Description</th></tr></thead>
          <tbody><tr><td>format</td><td><p>Choose unit:</p><ul><li>d for days</li><li>h for hours</li></ul></td></tr></tbody>
        </table>
        <h2><a name="Define"></a>Define General Settings</h2>
        <p>Set status to Enabled.</p>
      </div></body></html>
      """;

    const string videoHtml = """
      <html><body><div id="mc-main-content">
        <h1>Video: Adding Users</h1>
        <div><iframe src="https://players.brightcove.net/123/index.html?videoId=999"></iframe></div>
        <p>This video walks through adding users.</p>
      </div></body></html>
      """;

    var pages = new[]
    {
      CreatePage("/Help/Content/FAQs/AdministratorFAQs.htm", "Administrator FAQs", 0, ["FAQs > Administrator FAQs"], faqHtml),
      CreatePage("/Help/Content/Administrators/Apps/TriggersOutcomes/RESTAPI/ConfigureRESTAPIOutcome.htm", "Configure a REST API Outcome", 1, ["Administrators > REST API"], restApiHtml),
      CreatePage("/Help/Content/Videos/AddingUsersVideo.htm", "Video: Adding Users", 2, ["Videos > Adding Users"], videoHtml),
    };

    var converter = new MarkdownDocumentConverter();
    var corpus = await converter.ConvertCorpusAsync(pages, new ScraperOptions
    {
      BaseUrl = "https://example.com",
      Username = "u",
      Password = "p",
    }, TestContext.Current.CancellationToken);

    var faqDoc = corpus.Documents.Single(d => d.Route.Id == "faqs/administrator-faqs");
    Assert.Contains("audience:\n  - Administrators", faqDoc.MarkdownContent);
    Assert.Contains("# Administrator FAQs", faqDoc.MarkdownContent);
    Assert.Contains("## Access FAQs", faqDoc.MarkdownContent);
    Assert.Contains("### How do I get support?", faqDoc.MarkdownContent);
    Assert.Contains("Email support@onspring.com", faqDoc.MarkdownContent);
    Assert.Contains("[Define General Settings](../administrators/apps/triggers-outcomes/rest-api/configure-rest-api-outcome.md#define-general-settings)", faqDoc.MarkdownContent);
    Assert.DoesNotContain("transparent.gif", faqDoc.MarkdownContent);
    Assert.DoesNotContain("[email", faqDoc.MarkdownContent);
    Assert.DoesNotContain("\u00A0", faqDoc.MarkdownContent);
    Assert.DoesNotContain("\r\n", faqDoc.MarkdownContent);

    var restDoc = corpus.Documents.Single(d => d.Route.Id == "administrators/apps/triggers-outcomes/rest-api/configure-rest-api-outcome");
    Assert.Contains("`DateDiff(end_date, start_date, format)`", restDoc.MarkdownContent);
    Assert.Contains("> **IMPORTANT:** Test carefully before enabling.", restDoc.MarkdownContent);
    Assert.Contains("| Arg | Description |", restDoc.MarkdownContent);
    Assert.DoesNotContain("Resources/Images", restDoc.MarkdownContent);
    Assert.DoesNotContain("kanchor", restDoc.MarkdownContent);

    var videoDoc = corpus.Documents.Single(d => d.Route.Id == "videos/adding-users-video");
    Assert.Contains("[Watch embedded video](https://players.brightcove.net/123/index.html?videoId=999)", videoDoc.MarkdownContent);
  }

  private static FetchedPage CreatePage(string path, string title, int order, string[] tocPaths, string html)
  {
    var (_, requestPath) = MadCapTocParser.NormalizeContentUrl(path);
    return new FetchedPage(
      path,
      requestPath,
      html,
      new TocEntry(path, requestPath, title, order, tocPaths, InToc: true));
  }
}
