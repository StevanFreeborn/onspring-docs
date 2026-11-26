using HelpCenterScrapping.Options;
using HelpCenterScrapping.Processing;
using HelpCenterScrapping.Scraping;

namespace HelpCenterScrapping.Tests;

public sealed class PathMapperTests
{
  [Theory]
  [InlineData("/Help/Content/Home.htm", "home", "Home", "home")]
  [InlineData("/Help/Content/ReleaseNotes/5.0_New Features.htm", "release-notes/5.0-new-features", "Release Notes", "release-notes")]
  [InlineData("/Help/Content/Administrators/Apps/TriggersOutcomes/RESTAPI/ConfigureRESTAPIOutcome.htm", "administrators/apps/triggers-outcomes/rest-api/configure-rest-api-outcome", "Administrators", "guide")]
  [InlineData("/Help/Content/FAQs/AdministratorFAQs.htm", "faqs/administrator-faqs", "FAQs", "faq")]
  [InlineData("/Help/Content/AI/AIUseCases/AI_TPRM_UC.htm", "ai/ai-use-cases/ai-tprm-uc", "Onspring AI", "guide")]
  [InlineData("/Help/Content/Videos/AddingUsersVideo.htm", "videos/adding-users-video", "Videos", "video")]
  [InlineData("/Help/Content/GettingStarted/Glossary.htm", "getting-started/glossary", "Getting Started", "glossary")]
  public void MapRoutes_NormalizesPathsAndClassifiesDocuments(
    string sourcePath,
    string expectedId,
    string expectedSection,
    string expectedDocType)
  {
    var entry = new TocEntry(sourcePath, sourcePath, "Title", 0, [], true);
    var routes = PathMapper.MapRoutes([entry]);

    var route = Assert.Single(routes);
    Assert.Equal(expectedId, route.Id);
    Assert.Equal($"{expectedId}.md", route.RelativeMarkdownPath);
    Assert.Equal(expectedSection, route.Section);
    Assert.Equal(expectedDocType, route.DocType);
  }

  [Fact]
  public void MapRoutes_DisambiguatesCollisionsDeterministically()
  {
    var entries = new[]
    {
      new TocEntry("/Help/Content/EndUsers/My_Topic.htm", "/Help/Content/EndUsers/My_Topic.htm", "A", 0, [], true),
      new TocEntry("/Help/Content/EndUsers/MyTopic.htm", "/Help/Content/EndUsers/MyTopic.htm", "B", 1, [], true),
    };

    var routes = PathMapper.MapRoutes(entries);

    Assert.Equal(["end-users/my-topic", "end-users/my-topic-2"], routes.Select(r => r.Id).ToArray());
  }

  [Fact]
  public void FilterRoutes_RecentReleases_UsesNumericVersionSortingAndKeepsOverview()
  {
    var entries = new[]
    {
      "/Help/Content/Home.htm",
      "/Help/Content/ReleaseNotes/ReleaseNotes.htm",
      "/Help/Content/ReleaseNotes/2.0_NewFeatures.htm",
      "/Help/Content/ReleaseNotes/9.0_NewFeatures.htm",
      "/Help/Content/ReleaseNotes/10.0_NewFeatures.htm",
      "/Help/Content/ReleaseNotes/37.0_ReleaseNotes.htm",
      "/Help/Content/ReleaseNotes/37.2_ReleaseNotes.htm",
      "/Help/Content/Videos/AddingUsersVideo.htm",
    }.Select((p, idx) => new TocEntry(p, p, $"Title {idx}", idx, [], true));

    var routes = PathMapper.MapRoutes(entries);
    var options = new ScraperOptions
    {
      BaseUrl = "https://example.com",
      Username = "u",
      Password = "p",
      RecentReleasesCount = 2,
      ExcludeVideos = true,
    };

    var filtered = PathMapper.FilterRoutes(routes, options).Select(r => r.Id).ToList();

    Assert.Contains("home", filtered);
    Assert.Contains("release-notes/release-notes", filtered);
    Assert.Contains("release-notes/37.2-release-notes", filtered);
    Assert.Contains("release-notes/37.0-release-notes", filtered);
    Assert.DoesNotContain("release-notes/10.0-new-features", filtered);
    Assert.DoesNotContain("release-notes/9.0-new-features", filtered);
    Assert.DoesNotContain("videos/adding-users-video", filtered);
  }
}
