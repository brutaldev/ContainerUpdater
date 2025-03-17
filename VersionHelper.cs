using System.Text;
using System.Text.RegularExpressions;

namespace ContainerUpdater;

public static class VersionHelper
{
  public static string FindLatestMatchingVersion(string referenceVersion, IEnumerable<string> versions)
  {
    // Generate a regex pattern from the reference version.
    var pattern = GeneratePatternFromVersion(referenceVersion);

    // Filter versions that match the pattern.
    var matchingVersions = versions.Where(v => pattern.IsMatch(v));

    if (!matchingVersions.Any())
    {
      return referenceVersion;
    }

    if (matchingVersions.Count() == 1)
    {
      return matchingVersions.ElementAt(0);
    }

    // Parse and compare versions.
    var sortedVersions = matchingVersions
      .Select(v => new { Original = v, Parsed = ParseVersion(v) })
      .OrderByDescending(v => v.Parsed);

    return sortedVersions.First().Original;
  }

  private static Regex GeneratePatternFromVersion(string referenceVersion)
  {
    var versionParts = referenceVersion.Split('-', 2);
    var hasPrefix = referenceVersion.StartsWith("v", StringComparison.OrdinalIgnoreCase);
    var hasSuffix = versionParts.Length > 1;
    var pattern = new StringBuilder();

    if (hasPrefix)
    {
      pattern.Append("^v");
    }
    else
    {
      pattern.Append('^');
    }

    // Add version number pattern with the correct number of components.
    pattern.Append(@"(\d+)");
    for (var i = 1; i < versionParts[0].Split('.').Length; i++)
    {
      pattern.Append(@"\.(\d+)");
    }

    // Add suffix pattern if needed
    if (hasSuffix)
    {
      var suffix = versionParts[1];
      if (int.TryParse(suffix, out var _))
      {
        pattern.Append(@"-(\d+)");
      }
      else
      {
        pattern.Append($"-{Regex.Escape(suffix)}");
      }
    }

    pattern.Append('$');

    return new Regex(pattern.ToString(), RegexOptions.IgnoreCase);
  }

  private static Version ParseVersion(string version)
  {
    // Remove 'v' prefix if exists.
    if (version.StartsWith("v", StringComparison.OrdinalIgnoreCase))
    {
      version = version[1..];
    }

    // Split into version and suffix.
    var parts = version.Split('-', 2);
    var versionParts = parts[0].Split('.');

    int.TryParse(versionParts.ElementAtOrDefault(0), out var majorNumber);
    int.TryParse(versionParts.ElementAtOrDefault(1), out var minorNumber);
    int.TryParse(versionParts.ElementAtOrDefault(2), out var buildNumber);
    int.TryParse(parts.Length > 1 ? parts[1] : string.Empty, out var revisionNumber);

    return new Version(majorNumber, minorNumber, buildNumber, revisionNumber);
  }
}
