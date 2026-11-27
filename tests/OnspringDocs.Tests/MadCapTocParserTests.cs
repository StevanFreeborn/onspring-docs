using OnspringDocs.Scraping;

namespace OnspringDocs.Tests;

public sealed class MadCapTocParserTests
{
  [Fact]
  public void ParseHelpSystemXml_ExtractsMetadataAttributes()
  {
    const string xml = """
      <?xml version="1.0" encoding="utf-8"?>
      <WebHelpSystem DefaultUrl="Content/Home.htm" Toc="Data/Tocs/OnspringTOC.js" BuildTime="9/28/2026 4:28:27 PM" BuildVersion="22.0.9643.36696" />
      """;

    var meta = MadCapTocParser.ParseHelpSystemXml(xml);

    Assert.Equal("Data/Tocs/OnspringTOC.js", meta.TocRelativePath);
    Assert.Equal("Content/Home.htm", meta.DefaultUrl);
    Assert.Equal("22.0.9643.36696", meta.BuildVersion);
    Assert.Equal("9/28/2026 4:28:27 PM", meta.BuildTime);
  }

  [Fact]
  public void JsObjectLiteralToJson_HandlesSingleQuotesEscapesUnquotedKeysAndTrailingCommas()
  {
    const string payload = """
      define({'/Content/Home.htm':{i:[0],t:['What\'s "New" & Info'],b:[''],},tree:{n:[{i:0,c:0,}],},});
      """;

    var json = MadCapTocParser.JsObjectLiteralToJson(payload);
    using var doc = System.Text.Json.JsonDocument.Parse(json);

    Assert.Equal("What's \"New\" & Info", doc.RootElement.GetProperty("/Content/Home.htm").GetProperty("t")[0].GetString());
    Assert.Equal(0, doc.RootElement.GetProperty("tree").GetProperty("n")[0].GetProperty("i").GetInt32());
  }

  [Fact]
  public void ParseFromPayloads_ReconstructsOrderedTreeAndDeduplicatesEncodedUrls()
  {
    var helpSystem = new HelpSystemMetadata("Data/Tocs/OnspringTOC.js", "Content/Home.htm", "22.0", "9/28/2026");

    const string tocJs = """
      define({numchunks:2,prefix:'OnspringTOC_Chunk',tree:{n:[{i:0,c:0},{i:1,c:0,n:[{i:2,c:1}]},{i:3,c:1},{i:4,c:1,n:[{i:5,c:1}]}]}});
      """;

    const string chunk0 = """
      define({'/Content/Home.htm':{i:[0],t:['Getting Started'],b:['']},'/Content/Administrators/Apps/Fields/AttachmentField_Prod.htm':{i:[1],t:['Attachment Fields'],b:['']}});
      """;

    const string chunk1 = """
      define({'/Content/FAQs/PortalFAQs.htm':{i:[2],t:['Portal FAQs'],b:['']},'/Content/ReleaseNotes/5.0_New%20Features.htm#top':{i:[3],t:['5.0 New Features'],b:['']},'/Content/EndUsers/Dashboards/ViewDashboards.htm':{i:[5],t:['View Dashboards'],b:['']},'___':{i:[4],t:['Dashboards'],b:['']}});
      """;

    var result = MadCapTocParser.ParseFromPayloads(helpSystem, tocJs, [chunk0, chunk1]);

    Assert.Equal(5, result.EntriesByPath.Count);
    Assert.True(result.EntriesByPath.ContainsKey("/Help/Content/ReleaseNotes/5.0_New Features.htm"));
    Assert.DoesNotContain(result.EntriesByPath.Keys, k => k.Contains("___"));

    var portalFaq = result.EntriesByPath["/Help/Content/FAQs/PortalFAQs.htm"];
    Assert.Equal(2, portalFaq.TocOrder);
    Assert.Equal(["Attachment Fields > Portal FAQs"], portalFaq.TocPaths);

    var dashDoc = result.EntriesByPath["/Help/Content/EndUsers/Dashboards/ViewDashboards.htm"];
    Assert.Equal(["Dashboards > View Dashboards"], dashDoc.TocPaths);
  }
}
