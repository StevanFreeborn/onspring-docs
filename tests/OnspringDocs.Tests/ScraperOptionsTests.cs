using OnspringDocs.Options;

namespace OnspringDocs.Tests;

public sealed class ScraperOptionsTests
{
  [Fact]
  public void Parse_UsesExplicitConfigFileOverridingDefaults()
  {
    var tempDir = Path.Combine(
      Path.GetTempPath(),
      $"onspring-docs-opt-{Guid.NewGuid():N}"
    );
    Directory.CreateDirectory(tempDir);
    try
    {
      var cfgPath = Path.Combine(tempDir, "custom.json");
      File.WriteAllText(
        cfgPath,
        """
        {
          "Onspring": {
            "BaseUrl": "https://custom.onspring.com/",
            "Username": "alice",
            "Password": "secret"
          }
        }
        """
      );

      var result = ScraperOptions.Parse(
        ["--config", cfgPath, "--output", "out-dir", "--recent-releases", "3"]
      );

      Assert.True(result.IsSuccess);
      Assert.NotNull(result.Options);
      Assert.Equal("https://custom.onspring.com", result.Options.BaseUrl);
      Assert.Equal("alice", result.Options.Username);
      Assert.Equal("secret", result.Options.Password);
      Assert.Equal("out-dir", result.Options.OutputDirectory);
      Assert.Equal(3, result.Options.RecentReleasesCount);
      Assert.Equal(Path.GetFullPath(cfgPath), result.Options.ConfigPath);
    }
    finally
    {
      Directory.Delete(tempDir, recursive: true);
    }
  }

  [Fact]
  public void Parse_FailsWhenExplicitConfigMissing()
  {
    var missingPath = Path.Combine(
      Path.GetTempPath(),
      $"missing-{Guid.NewGuid():N}.json"
    );
    var result = ScraperOptions.Parse(["--config", missingPath]);

    Assert.False(result.IsSuccess);
    Assert.Contains("Config file not found", result.ErrorMessage, StringComparison.Ordinal);
  }
}
