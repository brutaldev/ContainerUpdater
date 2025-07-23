using System.Collections.Immutable;
using System.Reflection;
using Captin.ConsoleIntercept;
using CommandLine;
using ContainerUpdater;
using ContainerUpdater.Helpers;
using ContainerUpdater.Models;
using Docker.DotNet;
using Docker.DotNet.BasicAuth;
using Docker.DotNet.Models;

const string DockerRegistry = "index.docker.io";

const string DockerComposeProjectOnKey = "com.docker.compose.project";
const string DockerComposeDependsOnKey = "com.docker.compose.depends_on";

const string WatchtowerMonitorOnlyKey = "com.centurylinklabs.watchtower.monitor-only";
const string WatchtowerEnableKey = "com.centurylinklabs.watchtower.enable";
const string WatchtowerDependsOnKey = "com.centurylinklabs.watchtower.depends-on";
const string WatchtowerNoPullOnKey = "com.centurylinklabs.watchtower.no-pull";

// Steps to auto update base images and containers that use them:
// 1. Get all the current image digests and tags to perform a manifest lookup.
// 2. Lookup latest manifest and check if it matches the current image digest.
// 3. If not the latest, get the containers that are using the old/existing image and stop them.
// 4. Inspect and retain the information to re-install containers.
// 5. Remove the containers using the old image.
// 6. Remove the old image.
// 7. Pull the new image.
// 8. Re-create the containers from previous inspect data.
// 9. Start the containers if they were previously running.

var headerPadding = "".PadRight(10);
var headerApplication = $"Container Updater {Assembly.GetExecutingAssembly().GetName().Version}";
var dashLength = headerApplication.Length + (headerPadding.Length * 2) + 2;
Console.ForegroundColor = ConsoleColor.White;
Console.WriteLine();
Console.WriteLine("".PadRight(dashLength, '-'));
Console.WriteLine($"|{headerPadding}{headerApplication}{headerPadding}|");
Console.WriteLine("".PadRight(dashLength, '-'));
Console.WriteLine();
Console.ResetColor();

await Parser.Default.ParseArguments<Options>(args).WithParsedAsync(ExecuteAsync);

static async Task ExecuteAsync(Options options)
{
  Credentials dockerCredentials = (!string.IsNullOrEmpty(options.Username) || !string.IsNullOrEmpty(options.Password)) ?
    new BasicAuthCredentials(options.Username, options.Password) :
    new AnonymousCredentials();

  using var dockerConfiguration = (options.Host is not null) ?
    new DockerClientConfiguration(options.Host, dockerCredentials) :
    new DockerClientConfiguration(dockerCredentials);

  using var dockerClient = dockerConfiguration.CreateClient();

  using var consoleOut = ConsoleOut.Observe();
  var connected = false;
  var exitCode = 0;

  try
  {
    // Check if Docker is running.
    await dockerClient.System.PingAsync();
    connected = true;

    List<CheckImage> imagesToCheck = [];
    List<UpdateImage> imagesToUpdate = [];

    var version = await dockerClient.System.GetVersionAsync();

    Console.WriteLine("Docker Client Information");
    Console.WriteLine("-------------------------");
    Console.WriteLine($"Host    : {dockerConfiguration.EndpointBaseUri}");
    Console.WriteLine($"OS      : {version.Os} ({version.Arch})");
    Console.WriteLine($"Version : {version.Version}");
    Console.WriteLine($"API     : {version.APIVersion}");
    Console.WriteLine($"Kernel  : {version.KernelVersion}");
    Console.WriteLine();

    if (options.Exclude.Any())
    {
      Console.WriteLine($"EXCLUDE: {string.Join(" ", options.Exclude)}");
    }

    if (options.Include.Any())
    {
      Console.WriteLine($"INCLUDE: {string.Join(" ", options.Include)}");
    }

    if (options.DryRun)
    {
      Console.ForegroundColor = ConsoleColor.Yellow;
      Console.WriteLine("DRY RUN: No changes will be made to your images or containers");
      Console.WriteLine();
      Console.ResetColor();
    }

    // Get all images, parse them and create registry clients.
    foreach (var image in await dockerClient.Images.ListImagesAsync(new() { All = true }))
    {
      var repoDigest = image.RepoDigests.FirstOrDefault();
      var repoTag = image.RepoTags.FirstOrDefault();

      if (repoDigest is null)
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Image {image.ID} does not have a repository digest and will not be updated");
        Console.ResetColor();
        continue;
      }

      if (repoTag is null)
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Image {image.ID} does not have a repository tag and will not be updated");
        Console.ResetColor();
        continue;
      }

      // Parse the various properties to get the registry, repository, tag and digest.
      var digestParts = repoDigest.Split('@');
      var imageName = digestParts[0];
      var digest = digestParts[1];

      var repositoryParts = imageName.Split('/');
      if (repositoryParts.Length < 2)
      {
        repositoryParts = [DockerRegistry, "library", .. repositoryParts];
      }

      if (repositoryParts.Length < 3)
      {
        repositoryParts = [DockerRegistry, .. repositoryParts];
      }

      var registry = repositoryParts[0];
      var repository = string.Join('/', repositoryParts.Skip(1));

      var tagParts = repoTag.Split(':');
      var tag = tagParts[1];

      // Special handling for Docker.io
      if (registry.Equals("docker.io", StringComparison.OrdinalIgnoreCase))
      {
        registry = DockerRegistry;
      }

      imagesToCheck.Add(new(image.ID, imageName, repoTag, registry, repository, tag, digest));
    }

    Console.WriteLine($"Checking {imagesToCheck.Count} images across {imagesToCheck.DistinctBy(img => img.Registry).Count()} registries...");
    Console.WriteLine();

    // Get all containers to check their labels for Watchtower label-enable mode
    var allContainers = await dockerClient.Containers.ListContainersAsync(new() { All = true });
    var containerLabelMap = new Dictionary<string, IDictionary<string, string>>();

    foreach (var container in allContainers)
    {
      containerLabelMap[container.ImageID] = container.Labels ?? ImmutableDictionary<string, string>.Empty;
    }

    // Check if we're in label-enable mode (any container has the enable label)
    var isWatchtowerLabelEnable = containerLabelMap.Values
        .Any(labels => labels.ContainsKey(WatchtowerEnableKey) &&
                       labels[WatchtowerEnableKey]?.Equals("true", StringComparison.OrdinalIgnoreCase) == true);

    // Check each image for updates.
    foreach (var image in imagesToCheck)
    {
      Console.Write($"Checking image {image.OriginalTag}...");

      // Check if the image has been excluded by filter options
      if (options.Exclude.Contains(image.Repository, StringComparer.OrdinalIgnoreCase) ||
          image.Repository.Split('/').Any(x => options.Exclude.Contains(x, StringComparer.OrdinalIgnoreCase)))
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("EXCLUDED (FILTER OPTION)");
        Console.ResetColor();
        continue;
      }

      // Check if the image has been included by filter options
      if (options.Include.Any() &&
         !options.Include.Contains(image.Repository, StringComparer.OrdinalIgnoreCase) &&
         !image.Repository.Split('/').Any(x => options.Include.Contains(x, StringComparer.OrdinalIgnoreCase)))
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("EXCLUDED (FILTER OPTION)");
        Console.ResetColor();
        continue;
      }

      // Get container labels for this image (if any containers use this image)
      var containerLabels = containerLabelMap.TryGetValue(image.Id, out var containerLabelsValue) ? containerLabelsValue : ImmutableDictionary<string, string>.Empty;

      // Watchtower label-based exclusion logic
      var enableValue = containerLabels.TryGetValue(WatchtowerEnableKey, out var watchtowerEnableKeyValue) ? watchtowerEnableKeyValue : null;

      // If in label-enable mode, only process containers with enable=true
      if (isWatchtowerLabelEnable)
      {
        if (string.IsNullOrEmpty(enableValue) || !enableValue.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
          Console.ForegroundColor = ConsoleColor.Yellow;
          Console.WriteLine("EXCLUDED (WATCHTOWER LABEL)");
          Console.ResetColor();
          continue;
        }
      }
      // If not in label-enable mode, exclude containers with enable=false
      else if (!string.IsNullOrEmpty(enableValue) && enableValue.Equals("false", StringComparison.OrdinalIgnoreCase))
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("EXCLUDED (WATCHTOWER LABEL)");
        Console.ResetColor();
        continue;
      }

      try
      {
        // Check for monitor-only label on containers
        var isMonitorOnly = containerLabels.TryGetValue(WatchtowerMonitorOnlyKey, out var watchtowerMonitorOnlyKeyValue) &&
                            watchtowerMonitorOnlyKeyValue.Equals("true", StringComparison.OrdinalIgnoreCase);

        // Check for no-pull label on containers
        var isNoPull = containerLabels.TryGetValue(WatchtowerNoPullOnKey, out var watchtowerNoPullOnKeyValue) &&
                       watchtowerNoPullOnKeyValue.Equals("true", StringComparison.OrdinalIgnoreCase);

        var remoteDigests = await RegistryHelper.GetDigestsAsync(image.Registry, image.Repository, image.Tag);

        if (!isMonitorOnly && !isNoPull && !remoteDigests.Any(digest => digest == image.LocalDigest))
        {
          Console.ForegroundColor = ConsoleColor.Green;
          Console.WriteLine("UPDATE AVAILABLE (DIGEST)");
          Console.ResetColor();

          imagesToUpdate.Add(new(image.Id, image.OriginalName, image.OriginalTag, image.Tag, image.LocalDigest, remoteDigests.First()));
        }
        else if (isNoPull && !remoteDigests.Any(digest => digest == image.LocalDigest))
        {
          Console.ForegroundColor = ConsoleColor.Yellow;
          Console.WriteLine("EXCLUDED (NO-PULL LABEL)");
          Console.ResetColor();
        }
        else
        {
          var latestVersion = image.Tag;
          if (latestVersion.Contains('.'))
          {
            var tags = await RegistryHelper.GetTagsAsync(image.Registry, image.Repository);
            latestVersion = VersionHelper.FindLatestMatchingVersion(image.Tag, tags);
          }

          if (latestVersion != image.Tag)
          {
            if (options.DigestOnly || isMonitorOnly || isNoPull)
            {
              Console.ForegroundColor = ConsoleColor.Yellow;
              if (isNoPull)
              {
                Console.WriteLine($"EXCLUDED (NO-PULL LABEL, VERSION {latestVersion})");
              }
              else
              {
                Console.WriteLine($"EXCLUDED (VERSION {latestVersion})");
              }
              Console.ResetColor();
            }
            else
            {
              Console.ForegroundColor = ConsoleColor.Green;
              Console.WriteLine($"UPDATE AVAILABLE (VERSION {latestVersion})");
              Console.ResetColor();

              imagesToUpdate.Add(new(image.Id, image.OriginalName, image.OriginalTag, latestVersion, image.LocalDigest, string.Empty));
            }
          }
          else
          {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("NO UPDATE");
            Console.ResetColor();
          }
        }
      }
      catch (Exception ex)
      {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine("UPDATE CHECK FAILED");
        Console.ResetColor();

        Console.WriteLine();
        Console.WriteLine(ex.ToString());
        Console.WriteLine();
      }
    }

    // Update images.
    if (imagesToUpdate.Count > 0)
    {
      Console.ForegroundColor = ConsoleColor.Green;
      Console.WriteLine();
      Console.WriteLine($"Found {imagesToUpdate.Count} image updates.");
      Console.WriteLine();
      Console.ResetColor();

      var pullProgress = new Progress<JSONMessage>(_ => Console.Write("."));
      var dockerContainers = await dockerClient.Containers.ListContainersAsync(new() { All = true });

      // Group containers by image and build dependency information
      var containerUpdateGroups = new List<ContainerUpdateGroup>();

      foreach (var image in imagesToUpdate)
      {
        if (options.Interactive)
        {
          Console.WriteLine();
          Console.Write($"Update {image.OriginalTag}? [Y/N]: ");
          var choice = Console.ReadKey(true);

          while (choice.Key != ConsoleKey.Y && choice.Key != ConsoleKey.N)
          {
            choice = Console.ReadKey(true);
          }

          if (choice.Key == ConsoleKey.Y)
          {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("Yes");
            Console.ResetColor();
          }
          else if (choice.Key == ConsoleKey.N)
          {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("No");
            Console.ResetColor();
            continue;
          }
        }

        // Find all containers using the image and build dependency information
        var containersForImage = new List<ContainerInfo>();

        foreach (var container in dockerContainers.Where(c => c.ImageID == image.Id))
        {
          var inspect = await dockerClient.Containers.InspectContainerAsync(container.ID);
          var labels = container.Labels ?? ImmutableDictionary<string, string>.Empty;
          var project = labels.TryGetValue(DockerComposeProjectOnKey, out var dockerComposeProjectOnKeyValue) ? dockerComposeProjectOnKeyValue : string.Empty;
          var dependencies = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

          // Parse Docker Compose dependencies
          if (labels.TryGetValue(DockerComposeDependsOnKey, out var dockerComposeDependsOnKeyValue))
          {
            foreach (var dependencyName in dockerComposeDependsOnKeyValue
              .Split(',', StringSplitOptions.RemoveEmptyEntries)
              .Select(d => d.Trim().Split(':')[0]) // Remove condition if present (service:condition)
              .Where(d => !string.IsNullOrEmpty(d)))
            {
              // For Docker Compose, dependencies might be prefixed with project name
              dependencies.Add(string.IsNullOrEmpty(project) ? dependencyName : $"{project}_{dependencyName}");
              dependencies.Add(dependencyName); // Also add without prefix for flexibility
            }
          }

          // Parse Watchtower dependencies
          if (labels.TryGetValue(WatchtowerDependsOnKey, out var watchtowerDependsOnKeyValue))
          {
            foreach (var dependencyName in watchtowerDependsOnKeyValue
              .Split(',', StringSplitOptions.RemoveEmptyEntries)
              .Select(d => d.Trim())
              .Where(d => !string.IsNullOrEmpty(d)))
            {
              dependencies.Add(dependencyName);
            }
          }

          var containerConfig = new CreateContainerParameters
          {
            Image = $"{image.OriginalName}:{image.Tag}",
            HostConfig = inspect.HostConfig,
            Name = inspect.Name.TrimStart('/'),
            Env = inspect.Config.Env,
            Cmd = inspect.Config.Cmd,
            Entrypoint = inspect.Config.Entrypoint,
            WorkingDir = inspect.Config.WorkingDir,
            ExposedPorts = inspect.Config.ExposedPorts,
            Labels = inspect.Config.Labels,
            Volumes = inspect.Config.Volumes,
            Healthcheck = inspect.Config.Healthcheck,
            OnBuild = inspect.Config.OnBuild,
            StopSignal = inspect.Config.StopSignal,
            StopTimeout = inspect.Config.StopTimeout,
            Tty = inspect.Config.Tty,
            Shell = inspect.Config.Shell,
            User = inspect.Config.User,
            Hostname = inspect.Config.Hostname,
            Domainname = inspect.Config.Domainname,
            AttachStdin = inspect.Config.AttachStdin,
            AttachStdout = inspect.Config.AttachStdout,
            AttachStderr = inspect.Config.AttachStderr,
            StdinOnce = inspect.Config.StdinOnce,
            OpenStdin = inspect.Config.OpenStdin,
            ArgsEscaped = inspect.Config.ArgsEscaped,
            NetworkDisabled = inspect.Config.NetworkDisabled,
            MacAddress = inspect.NetworkSettings.MacAddress,
            NetworkingConfig = new NetworkingConfig
            {
              EndpointsConfig = inspect.NetworkSettings.Networks,
            },
          };

          containersForImage.Add(new ContainerInfo(
            container.ID,
            inspect.Name.TrimStart('/'),
            inspect.State.Running,
            containerConfig,
            dependencies,
            project));
        }

        if (containersForImage.Count > 0)
        {
          containerUpdateGroups.Add(new ContainerUpdateGroup(
            image.Id,
            image.OriginalName,
            image.Tag,
            containersForImage));
        }
      }

      // Process each update group
      foreach (var updateGroup in containerUpdateGroups)
      {
        Console.WriteLine($"Processing containers for image {updateGroup.ImageName}");

        // Sort containers by dependencies for proper stop order (dependencies first)
        var sortedContainers = SortContainersByDependencies(updateGroup.Containers);

        // Stop and remove containers in dependency order
        foreach (var container in sortedContainers)
        {
          try
          {
            if (container.IsRunning)
            {
              Console.WriteLine($"Stopping container {container.Name} ({container.Id})...");

              if (!options.DryRun && !await dockerClient.Containers.StopContainerAsync(container.Id, new() { WaitBeforeKillSeconds = 15 }))
              {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("Failed to stop container, killing it instead...");
                Console.ResetColor();

                await dockerClient.Containers.KillContainerAsync(container.Id, new());
              }
            }

            if (!options.DryRun)
            {
              await dockerClient.Containers.RemoveContainerAsync(container.Id, new() { Force = true, RemoveLinks = false, RemoveVolumes = false });
            }
          }
          catch (Exception ex)
          {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("CONTAINER REMOVAL FAILED");
            Console.ResetColor();

            Console.WriteLine();
            Console.WriteLine(ex.ToString());
            Console.WriteLine();
          }
        }

        // Remove old image and pull new one
        try
        {
          var imageToUpdate = imagesToUpdate.First(img => img.Id == updateGroup.ImageId);

          Console.WriteLine($"Removing old image for {imageToUpdate.OriginalTag}");
          Console.WriteLine(imageToUpdate.LocalDigest);

          if (!options.DryRun)
          {
            await dockerClient.Images.DeleteImageAsync(updateGroup.ImageId, new() { Force = true, NoPrune = true });
          }

          Console.WriteLine($"Pulling new image for {updateGroup.ImageName}:{updateGroup.NewTag}");

          if (!string.IsNullOrEmpty(imageToUpdate.NewDigest))
          {
            Console.WriteLine(imageToUpdate.NewDigest);
          }

          if (!options.DryRun)
          {
            await dockerClient.Images.CreateImageAsync(new() { FromImage = updateGroup.ImageName, Tag = updateGroup.NewTag }, null, pullProgress);
            Console.WriteLine();
            Console.WriteLine();
          }
          else
          {
            Console.WriteLine("".PadRight(50, '.'));
            Console.WriteLine();
          }
        }
        catch (Exception ex)
        {
          Console.ForegroundColor = ConsoleColor.Red;
          Console.WriteLine("REMOVING/PULLING NEW FAILED");
          Console.ResetColor();

          Console.WriteLine();
          Console.WriteLine(ex.ToString());
          Console.WriteLine();
          continue; // Skip container recreation if image update failed
        }

        // Recreate and start containers in reverse dependency order (dependents first)
        foreach (var container in sortedContainers.AsEnumerable().Reverse())
        {
          try
          {
            Console.WriteLine($"Restoring container {container.Name}...");

            if (!options.DryRun)
            {
              // Update the image reference in the container config
              container.CreateParameters.Image = $"{updateGroup.ImageName}:{updateGroup.NewTag}";

              var newContainer = await dockerClient.Containers.CreateContainerAsync(container.CreateParameters);

              if (container.IsRunning)
              {
                Console.WriteLine($"Starting container {container.Name} ({newContainer.ID})...");
                await dockerClient.Containers.StartContainerAsync(newContainer.ID, new());

                // Small delay to allow container to start before starting dependents
                await Task.Delay(1000);
              }
            }
          }
          catch (Exception ex)
          {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("CONTAINER CREATION FAILED");
            Console.ResetColor();

            Console.WriteLine();
            Console.WriteLine(ex.ToString());
            Console.WriteLine();
          }
        }

        Console.WriteLine($"Completed update for {updateGroup.ImageName}");
        Console.WriteLine();
      }
    }
    else
    {
      Console.ForegroundColor = ConsoleColor.Green;
      Console.WriteLine();
      Console.WriteLine("Nothing to update :)");
      Console.WriteLine();
      Console.ResetColor();
    }
  }
  catch (Exception ex)
  {
    if (connected)
    {
      Console.ForegroundColor = ConsoleColor.Red;
      Console.WriteLine();
      Console.WriteLine(ex.ToString());
      Console.ResetColor();

      exitCode = 1;
    }
    else
    {
      Console.ForegroundColor = ConsoleColor.Yellow;
      Console.WriteLine("Docker engine is not running :(");
      Console.ResetColor();

      exitCode = 2;
    }
  }
  finally
  {
    await File.AppendAllTextAsync("ContainerUpdater.log", $"--- {DateTime.Now:yyyy/MMdd HH:mm:ss} ---{Environment.NewLine}{consoleOut}{Environment.NewLine}");
  }

  if (options.Interactive)
  {
    Console.WriteLine();
    Console.WriteLine("Press any key to exit...");
    Console.ReadKey(true);
  }

  Environment.Exit(exitCode);
}

static List<ContainerInfo> SortContainersByDependencies(List<ContainerInfo> containers)
{
  var sorted = new List<ContainerInfo>();
  var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  var containerMap = containers.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);

  void Visit(ContainerInfo container)
  {
    if (visiting.Contains(container.Name))
    {
      Console.WriteLine($"Warning: Circular dependency detected involving container '{container.Name}'");
      return;
    }

    if (visited.Contains(container.Name))
    {
      return;
    }

    visiting.Add(container.Name);

    // Visit dependencies first (for stopping order)
    foreach (var dependency in container.Dependencies)
    {
      if (containerMap.TryGetValue(dependency, out var depContainer))
      {
        Visit(depContainer);
      }
    }

    visiting.Remove(container.Name);
    visited.Add(container.Name);
    sorted.Add(container);
  }

  foreach (var container in containers)
  {
    Visit(container);
  }

  return sorted;
}
