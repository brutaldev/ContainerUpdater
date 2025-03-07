using System.Net;
using System.Text;
using System.Text.Json;

namespace ContainerUpdater;

public static class RegistryManifestHelper
{
  private const string RegularManifestAcceptHeader = "application/vnd.docker.distribution.manifest.v2+json, application/vnd.oci.image.manifest.v1+json, application/vnd.oci.image.index.v1+json";
  private const string FatManifestAcceptHeader = "application/vnd.docker.distribution.manifest.list.v2+json";
  private const string DockerContentDigestHeader = "Docker-Content-Digest";

  private static readonly HttpClient fallbackRegistryClient = new();

  public static async Task<IEnumerable<string>> GetDigestsAsync(string registry, string repository, string tag, CancellationToken cancellationToken = default)
  {
    var authHeader = string.Empty;
    var requestUrl = $"https://{registry}/v2/{repository}/manifests/{tag}";

    var (registryUsername, registryPassword) = await CredentialHelper.GetCredentialsAsync(registry);
    if (!string.IsNullOrEmpty(registryUsername) && !string.IsNullOrEmpty(registryPassword))
    {
      authHeader = "Basic" + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{registryUsername}:{registryPassword}"));
    }

    // Attempt a regular manifest request first.
    var manifestRequest = new HttpRequestMessage(HttpMethod.Head, requestUrl);
    manifestRequest.Headers.TryAddWithoutValidation("Accept", RegularManifestAcceptHeader);

    if (!string.IsNullOrEmpty(authHeader))
    {
      manifestRequest.Headers.TryAddWithoutValidation("Authorization", authHeader);
    }

    var manifestResponse = await fallbackRegistryClient.SendAsync(manifestRequest, cancellationToken);
    if (manifestResponse.StatusCode == HttpStatusCode.Unauthorized)
    {
      authHeader = string.Empty;

      manifestResponse.Headers.TryGetValues("WWW-Authenticate", out var challengeHeaders);
      var challengeHeader = challengeHeaders?.FirstOrDefault();

      if (!string.IsNullOrEmpty(challengeHeader))
      {
        // Build the challenge URL.
        var parts = challengeHeader.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2)
        {
          var scheme = parts[0];
          // Only supporting bearer token generation here.
          if (scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
          {
            var realm = string.Empty;
            var parameters = new Dictionary<string, string>();
            foreach (var parameter in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
              var parameterParts = parameter.Split('=');
              if (parameterParts[0].Equals("realm", StringComparison.OrdinalIgnoreCase))
              {
                realm = parameterParts[1].Trim('"');
              }
              else
              {
                parameters.Add(parameterParts[0], parameterParts[1].Trim('"'));
              }
            }

            // Only continue if we got a realm to request a token from.
            if (!string.IsNullOrEmpty(realm))
            {
              var authUrl = $"{realm}?{string.Join("&", parameters.Select(p => $"{p.Key}={p.Value}"))}";
              var authRequest = new HttpRequestMessage(HttpMethod.Get, authUrl);
              var authResponse = await fallbackRegistryClient.SendAsync(authRequest, cancellationToken);
              if (authResponse.IsSuccessStatusCode)
              {
                using var json = JsonDocument.Parse(await authResponse.Content.ReadAsStringAsync(cancellationToken));
                var root = json.RootElement;
                if (root.TryGetProperty("access_token", out var accessToken))
                {
                  authHeader = "Bearer " + accessToken;
                }
                else if (root.TryGetProperty("token", out var token))
                {
                  authHeader = "Bearer " + token;
                }
                else
                {
                  throw new Exception("Could not find token from authentication JSON response.");
                }
              }
            }
            else
            {
              throw new Exception("Could determine the realm for WWW authentication.");
            }
          }
          else
          {
            throw new Exception($"Scheme '{scheme}' is not supported yet.");
          }
        }
      }
      else
      {
        throw new Exception("No 'WWW-Authenticate' header supplied in 401 manifest lookup response.");
      }

      if (!string.IsNullOrEmpty(authHeader))
      {
        var retryManifestRequest = new HttpRequestMessage(HttpMethod.Head, requestUrl);
        retryManifestRequest.Headers.TryAddWithoutValidation("Accept", RegularManifestAcceptHeader);
        retryManifestRequest.Headers.TryAddWithoutValidation("Authorization", authHeader);
        manifestResponse = await fallbackRegistryClient.SendAsync(retryManifestRequest, cancellationToken);
      }
    }

    manifestResponse.Headers.TryGetValues(DockerContentDigestHeader, out var manifestDigestHeaders);
    var manifestDigestHeader = manifestDigestHeaders?.FirstOrDefault();

    var digests = new HashSet<string>();
    if (!string.IsNullOrEmpty(manifestDigestHeader))
    {
      digests.Add(manifestDigestHeader);
    }

    var fatManifestRequest = new HttpRequestMessage(HttpMethod.Head, requestUrl);
    fatManifestRequest.Headers.TryAddWithoutValidation("Accept", FatManifestAcceptHeader);

    if (!string.IsNullOrEmpty(authHeader))
    {
      fatManifestRequest.Headers.TryAddWithoutValidation("Authorization", authHeader);
    }

    var fatManifestResponse = await fallbackRegistryClient.SendAsync(fatManifestRequest, cancellationToken);
    fatManifestResponse.Headers.TryGetValues(DockerContentDigestHeader, out var fatManifestDigestHeaders);
    var fatManifestDigestHeader = fatManifestDigestHeaders?.FirstOrDefault();

    if (!string.IsNullOrEmpty(fatManifestDigestHeader))
    {
      digests.Add(fatManifestDigestHeader);
    }

    if (digests.Count == 0)
    {
      throw new Exception($"Could not find Docker-Content-Digest header for URL {requestUrl}");
    }

    return digests;
  }
}
