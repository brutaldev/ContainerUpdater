using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ContainerUpdater.Helpers;

public static partial class RegistryHelper
{
  private const string RegularManifestAcceptHeader = "application/vnd.docker.distribution.manifest.v2+json, application/vnd.oci.image.manifest.v1+json, application/vnd.oci.image.index.v1+json";
  private const string FatManifestAcceptHeader = "application/vnd.docker.distribution.manifest.list.v2+json";
  private const string DockerContentDigestHeader = "Docker-Content-Digest";

  private static readonly ConcurrentDictionary<string, string> AuthCache = new(StringComparer.OrdinalIgnoreCase);
  private static readonly HttpClient dockerRegistryClient = new();

  public static async Task<IEnumerable<string>> GetTagsAsync(string registry, string repository, CancellationToken cancellationToken = default)
  {
    var tags = new List<string>();
    var requestUrl = $"https://{registry}/v2/{repository}/tags/list?n=100";
    var cacheKey = $"{registry}/{repository}";

    var (tagsResponse, authHeader) = await SendAuthenticatedAsync(HttpMethod.Get, requestUrl, "application/json", cacheKey, cancellationToken);

    tagsResponse.EnsureSuccessStatusCode();

    using (var doc = JsonDocument.Parse(await tagsResponse.Content.ReadAsStringAsync(cancellationToken)))
    {
      var root = doc.RootElement;
      tags.AddRange(root.GetProperty("tags").Deserialize<IEnumerable<string>>() ?? []);
    }

    // Check if there are more tags to fetch.
    while (tagsResponse.Headers.TryGetValues("Link", out var linkHeaders) && !string.IsNullOrEmpty(linkHeaders?.FirstOrDefault()))
    {
      var linkHeader = linkHeaders.First();
      var urlMatch = LinkHeaderUrlRegex().Match(linkHeader);
      var relMatch = LinkHeaderRelRegex().Match(linkHeader);

      if (urlMatch.Success && relMatch.Success)
      {
        var nextUrl = urlMatch.Groups[1].Value;
        var rel = relMatch.Groups[1].Value;

        if (rel.Equals("next", StringComparison.OrdinalIgnoreCase))
        {
          var nextTagsRequest = new HttpRequestMessage(HttpMethod.Get, $"https://{registry}{nextUrl}");
          nextTagsRequest.Headers.TryAddWithoutValidation("Accept", "application/json");
          nextTagsRequest.Headers.TryAddWithoutValidation("Authorization", authHeader);
          tagsResponse = await dockerRegistryClient.SendAsync(nextTagsRequest, cancellationToken);

          tagsResponse.EnsureSuccessStatusCode();

          using var doc = JsonDocument.Parse(await tagsResponse.Content.ReadAsStringAsync(cancellationToken));
          var root = doc.RootElement;
          tags.AddRange(root.GetProperty("tags").Deserialize<IEnumerable<string>>() ?? []);
        }
        else
        {
          break;
        }
      }
    }

    return tags;
  }

  public static async Task<IEnumerable<string>> GetDigestsAsync(string registry, string repository, string tag, CancellationToken cancellationToken = default)
  {
    var requestUrl = $"https://{registry}/v2/{repository}/manifests/{tag}";
    var cacheKey = $"{registry}/{repository}";

    var (manifestResponse, authHeader) = await SendAuthenticatedAsync(HttpMethod.Head, requestUrl, RegularManifestAcceptHeader, cacheKey, cancellationToken);

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

    var fatManifestResponse = await dockerRegistryClient.SendAsync(fatManifestRequest, cancellationToken);
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

  /// <summary>
  /// Sends an authenticated HTTP request, handling the 401 Bearer challenge flow and caching the token.
  /// </summary>
  private static async Task<(HttpResponseMessage Response, string? AuthHeader)> SendAuthenticatedAsync(
    HttpMethod method, string requestUrl, string acceptHeader, string cacheKey, CancellationToken cancellationToken)
  {
    AuthCache.TryGetValue(cacheKey, out var authHeader);

    var request = new HttpRequestMessage(method, requestUrl);
    request.Headers.TryAddWithoutValidation("Accept", acceptHeader);

    if (string.IsNullOrEmpty(authHeader))
    {
      var (registryUsername, registryPassword) = await CredentialHelper.GetCredentialsAsync(cacheKey);
      if (!string.IsNullOrEmpty(registryUsername) && !string.IsNullOrEmpty(registryPassword))
      {
        authHeader = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{registryUsername}:{registryPassword}"));
        request.Headers.TryAddWithoutValidation("Authorization", authHeader);
      }
    }
    else
    {
      request.Headers.TryAddWithoutValidation("Authorization", authHeader);
    }

    var response = await dockerRegistryClient.SendAsync(request, cancellationToken);

    if (response.StatusCode == HttpStatusCode.Unauthorized)
    {
      authHeader = await GetAuthHeaderUsingChallengeHeaderAsync(response.Headers, cancellationToken);

      if (!string.IsNullOrEmpty(authHeader))
      {
        var retryRequest = new HttpRequestMessage(method, requestUrl);
        retryRequest.Headers.TryAddWithoutValidation("Accept", acceptHeader);
        retryRequest.Headers.TryAddWithoutValidation("Authorization", authHeader);
        response = await dockerRegistryClient.SendAsync(retryRequest, cancellationToken);
      }
    }

    AuthCache.TryAdd(cacheKey, authHeader ?? string.Empty);

    return (response, authHeader);
  }

  private static async Task<string> GetAuthHeaderUsingChallengeHeaderAsync(HttpResponseHeaders headers, CancellationToken cancellationToken)
  {
    headers.TryGetValues("WWW-Authenticate", out var challengeHeaders);

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
            var authResponse = await dockerRegistryClient.SendAsync(authRequest, cancellationToken);
            if (authResponse.IsSuccessStatusCode)
            {
              using var json = JsonDocument.Parse(await authResponse.Content.ReadAsStringAsync(cancellationToken));
              var root = json.RootElement;
              if (root.TryGetProperty("access_token", out var accessToken))
              {
                return "Bearer " + accessToken;
              }
              else if (root.TryGetProperty("token", out var token))
              {
                return "Bearer " + token;
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

    throw new Exception("No 'WWW-Authenticate' header supplied in 401 manifest lookup response.");
  }

  [GeneratedRegex(@"<([^>]+)>")]
  private static partial Regex LinkHeaderUrlRegex();

  [GeneratedRegex(@"rel\s*=\s*""([^""]+)""")]
  private static partial Regex LinkHeaderRelRegex();
}

