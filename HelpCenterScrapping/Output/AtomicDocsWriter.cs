using System.Text;

namespace HelpCenterScrapping.Output;

/// <summary>
/// Writes the generated documentation tree into a staging directory using UTF-8 (no BOM) and LF line endings,
/// then atomically swaps it into the final output path so stale files never linger and failures never corrupt existing docs.
/// </summary>
public static class AtomicDocsWriter
{
  private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

  public static void WriteAllAtomically(
    string outputDirectory,
    IReadOnlyDictionary<string, string> filesByRelativePosixPath)
  {
    var targetPath = Path.GetFullPath(outputDirectory);
    var parentDir = Path.GetDirectoryName(targetPath)
      ?? throw new InvalidOperationException($"Cannot determine parent directory for '{targetPath}'.");

    Directory.CreateDirectory(parentDir);

    var stagingPath = $"{targetPath}.__staging";
    var backupPath = $"{targetPath}.__backup";

    TryDeleteDirectory(stagingPath);
    TryDeleteDirectory(backupPath);

    try
    {
      Directory.CreateDirectory(stagingPath);

      foreach (var kv in filesByRelativePosixPath.OrderBy(k => k.Key, StringComparer.Ordinal))
      {
        var relativeOsPath = kv.Key.Replace('/', Path.DirectorySeparatorChar);
        var fullFilePath = Path.Combine(stagingPath, relativeOsPath);
        var fileDir = Path.GetDirectoryName(fullFilePath);
        if (!string.IsNullOrEmpty(fileDir))
        {
          Directory.CreateDirectory(fileDir);
        }

        var normalizedContent = kv.Value.Replace("\r\n", "\n").Replace('\r', '\n');
        File.WriteAllText(fullFilePath, normalizedContent, Utf8NoBom);
      }

      var targetExisted = Directory.Exists(targetPath);
      if (targetExisted)
      {
        Directory.Move(targetPath, backupPath);
      }

      try
      {
        Directory.Move(stagingPath, targetPath);
      }
      catch
      {
        if (targetExisted && Directory.Exists(backupPath) && !Directory.Exists(targetPath))
        {
          Directory.Move(backupPath, targetPath);
        }
        throw;
      }

      if (Directory.Exists(backupPath))
      {
        TryDeleteDirectory(backupPath);
      }
    }
    finally
    {
      TryDeleteDirectory(stagingPath);
    }
  }

  private static void TryDeleteDirectory(string path)
  {
    if (Directory.Exists(path))
    {
      Directory.Delete(path, recursive: true);
    }
  }
}
