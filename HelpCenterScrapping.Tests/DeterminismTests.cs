using HelpCenterScrapping.Options;
using HelpCenterScrapping.Output;
using HelpCenterScrapping.Processing;
using HelpCenterScrapping.Scraping;

namespace HelpCenterScrapping.Tests;

public sealed class DeterminismTests
{
  [Fact]
  public async Task FullPipeline_ProducesByteForByteIdenticalOutputRegardlessOfInputOrder()
  {
    var helpSystem = new HelpSystemMetadata("Data/Tocs/OnspringTOC.js", "Content/Home.htm", "22.0.1", "9/28/2026 4:00 PM");
    const string tocJs = """
      define({numchunks:1,prefix:'Chunk',tree:{n:[{i:0,c:0},{i:1,c:0},{i:2,c:0},{i:3,c:0}]}});
      """;
    const string chunk0 = """
      define({
        '/Content/Home.htm':{i:[0],t:['Home'],b:['']},
        '/Content/Administrators/Apps/AppsOverview.htm':{i:[1],t:['Apps Overview'],b:['']},
        '/Content/ReleaseNotes/37.2_ReleaseNotes.htm':{i:[2],t:['37.2 Release Notes'],b:['']},
        '/Content/Videos/AddingUsersVideo.htm':{i:[3],t:['Adding Users'],b:['']}
      });
      """;

    var tocResult = MadCapTocParser.ParseFromPayloads(helpSystem, tocJs, [chunk0]);

    var pagesInOrder = new List<FetchedPage>
    {
      MakePage(tocResult, "/Help/Content/Home.htm", "<html><body><div id='mc-main-content'><h5>Welcome to the Onspring Help Center</h5><p>Start here.</p><p>Help Center version 37.2.0</p></div></body></html>"),
      MakePage(tocResult, "/Help/Content/Administrators/Apps/AppsOverview.htm", "<html><body><div id='mc-main-content'><h1>Apps Overview</h1><p><b>Topic Audience:</b>&#160;Administrators</p><p>Apps structure your data. See <a href='../../ReleaseNotes/37.2_ReleaseNotes.htm'>37.2 Release Notes</a>.</p></div></body></html>"),
      MakePage(tocResult, "/Help/Content/ReleaseNotes/37.2_ReleaseNotes.htm", "<html><body><div id='mc-main-content'><h1>37.2 Release Notes</h1><p>Released September 2026.</p></div></body></html>"),
      MakePage(tocResult, "/Help/Content/Videos/AddingUsersVideo.htm", "<html><body><div id='mc-main-content'><h1>Video: Adding Users</h1><iframe src='https://players.brightcove.net/1/v.html'></iframe><p>Watch how to add users.</p></div></body></html>"),
    };

    var pagesReversed = pagesInOrder.AsEnumerable().Reverse().ToList();

    var options = new ScraperOptions
    {
      BaseUrl = "https://example.com",
      Username = "u",
      Password = "p",
    };

    var converter = new MarkdownDocumentConverter();
    var corpusA = await converter.ConvertCorpusAsync(pagesInOrder, options, TestContext.Current.CancellationToken);
    var corpusB = await converter.ConvertCorpusAsync(pagesReversed, options, TestContext.Current.CancellationToken);

    var filesA = BuildAllOutputFiles(tocResult, corpusA, options);
    var filesB = BuildAllOutputFiles(tocResult, corpusB, options);

    Assert.Equal(filesA.Keys.OrderBy(k => k, StringComparer.Ordinal), filesB.Keys.OrderBy(k => k, StringComparer.Ordinal));
    foreach (var key in filesA.Keys)
    {
      Assert.Equal(filesA[key], filesB[key]);
      Assert.DoesNotContain("\r\n", filesA[key]);
    }
  }

  private static FetchedPage MakePage(MadCapTocResult toc, string canonicalPath, string html)
  {
    var entry = toc.EntriesByPath[canonicalPath];
    return new FetchedPage(canonicalPath, entry.RequestPath, html, entry);
  }

  private static Dictionary<string, string> BuildAllOutputFiles(
    MadCapTocResult toc,
    CorpusProcessingResult corpus,
    ScraperOptions options)
  {
    var files = corpus.Documents.ToDictionary(
      d => d.Route.RelativeMarkdownPath,
      d => d.MarkdownContent,
      StringComparer.Ordinal);

    files["AGENTS.md"] = IndexGenerator.GenerateAgentsMd(corpus);
    files["INDEX.md"] = IndexGenerator.GenerateIndexMd(toc, corpus);
    files["llms.txt"] = IndexGenerator.GenerateLlmsTxt(corpus);
    files["manifest.json"] = ManifestGenerator.GenerateManifestJson(toc, corpus, options);
    return files;
  }
}
