using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace ContainerUpdater;

public static class CredentialHelper
{
  private static readonly JsonElement? configJson;

#pragma warning disable S3963 // "static" fields should be initialized inline
  static CredentialHelper()
  {
    var configPath = string.Empty;

    // Check for DOCKER_CONFIG environment variable first.
    var dockerConfig = Environment.GetEnvironmentVariable("DOCKER_CONFIG");
    if (!string.IsNullOrEmpty(dockerConfig))
    {
      configPath = Path.Combine(dockerConfig, "config.json");
    }
    else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
    {
      var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
      configPath = Path.Combine(userProfile, ".docker", "config.json");
    }
    else
    {
      var home = Environment.GetEnvironmentVariable("HOME");
      if (!string.IsNullOrEmpty(home))
      {
        configPath = Path.Combine(home, ".docker", "config.json");
      }
    }

    if (!string.IsNullOrEmpty(configPath) && File.Exists(configPath))
    {
      var configJsonText = File.ReadAllText(configPath);

      if (!string.IsNullOrEmpty(configJsonText))
      {
        using var doc = JsonDocument.Parse(configJsonText);
        configJson = doc.RootElement.Clone();
      }
    }
  }
#pragma warning restore S3963 // "static" fields should be initialized inline

  public static async Task<(string? Username, string? Password)> GetCredentialsAsync(string registry)
  {
    if (!configJson.HasValue)
    {
      return (null, null);
    }

    // Check direct auth section (older Docker versions).
    if (configJson.Value.TryGetProperty("auths", out var auths) && auths.TryGetProperty(registry, out var registryProperty))
    {
      // Check for basic auth (base64 encoded).
      if (registryProperty.TryGetProperty("auth", out var authProperty))
      {
        var authString = authProperty.GetString();
        if (!string.IsNullOrEmpty(authString))
        {
          var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authString));
          var parts = decoded.Split(':', 2, StringSplitOptions.RemoveEmptyEntries);
          if (parts.Length != 2)
          {
            return (null, null);
          }

          return (parts[0], parts[1]);
        }
      }

      // Check if there are explicit username/password properties.
      if (registryProperty.TryGetProperty("username", out var usernameProperty) &&
          registryProperty.TryGetProperty("password", out var passwordProperty))
      {
        return (usernameProperty.GetString(), passwordProperty.GetString());
      }
    }

    try
    {
      // Check for registry specific credential helpers.
      if (configJson.Value.TryGetProperty("credHelpers", out var credHelpers) &&
          credHelpers.TryGetProperty(registry, out var helperName))
      {
        return await GetCredentialsFromHelperAsync(helperName.GetString(), registry);
      }

      // Check for default credential helper.
      if (configJson.Value.TryGetProperty("credsStore", out var credsStore))
      {
        return await GetCredentialsFromHelperAsync(credsStore.GetString(), registry);
      }
    }
    catch (Win32Exception)
    {
      // Ignore any exceptions from the helper process.
    }

    return (null, null);
  }

  private static async Task<(string? Username, string? Password)> GetCredentialsFromHelperAsync(string? helperName, string registry)
  {
    var helperCommand = $"docker-credential-{helperName}";

    using var process = new Process
    {
      StartInfo = new ProcessStartInfo
      {
        FileName = helperCommand,
        Arguments = "get",
        RedirectStandardInput = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true
      }
    };

    process.Start();

    // Write registry URL to stdin.
    await process.StandardInput.WriteAsync(registry);
    process.StandardInput.Close();

    // Read helper output from stdout.
    var output = await process.StandardOutput.ReadToEndAsync();
    await process.WaitForExitAsync();

    if (process.ExitCode == 0)
    {
      using var doc = JsonDocument.Parse(output);
      var root = doc.RootElement;

      return (root.GetProperty("Username").GetString(), root.GetProperty("Secret").GetString());
    }

    return (null, null);
  }
}
