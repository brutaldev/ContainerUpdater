using System.Reflection;
using Captin.ConsoleIntercept;
using CommandLine;
using ContainerUpdater;
using Docker.DotNet;
using Docker.DotNet.BasicAuth;
using Docker.DotNet.Models;

const string DockerRegistry = "index.docker.io";

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

    List<(string Id, string OriginalName, string OriginalTag, string Registry, string Repository, string Tag, string LocalDigest)> imagesToCheck = [];
    List<(string Id, string OriginalName, string OriginalTag, string Tag, string LocalDigest, string NewDigest)> imagesToUpdate = [];

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

    // Check each image for updates.
    foreach (var image in imagesToCheck)
    {
      Console.Write($"Checking image {image.OriginalTag}... ");

      // Check if the image has been excluded.
      if (options.Exclude.Contains(image.Repository, StringComparer.OrdinalIgnoreCase) ||
          image.Repository.Split('/').Any(x => options.Exclude.Contains(x, StringComparer.OrdinalIgnoreCase)))
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("EXCLUDED");
        Console.ResetColor();

        continue;
      }

      // Check if the image has been included.
      if (options.Include.Any() &&
         !options.Include.Contains(image.Repository, StringComparer.OrdinalIgnoreCase) &&
         !image.Repository.Split('/').Any(x => options.Include.Contains(x, StringComparer.OrdinalIgnoreCase)))
      {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("EXCLUDED");
        Console.ResetColor();

        continue;
      }

      try
      {
        var remoteDigests = await RegistryHelper.GetDigestsAsync(image.Registry, image.Repository, image.Tag);

        if (!remoteDigests.Any(digest => digest == image.LocalDigest))
        {
          Console.ForegroundColor = ConsoleColor.Green;
          Console.WriteLine("UPDATE AVAILABLE (DIGEST)");
          Console.ResetColor();

          imagesToUpdate.Add((image.Id, image.OriginalName, image.OriginalTag, image.Tag, image.LocalDigest, remoteDigests.First()));
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
            if (options.DigestOnly)
            {
              Console.ForegroundColor = ConsoleColor.Yellow;
              Console.WriteLine($"EXCLUDED (VERSION {latestVersion})");
              Console.ResetColor();
            }
            else
            {
              Console.ForegroundColor = ConsoleColor.Green;
              Console.WriteLine($"UPDATE AVAILABLE (VERSION {latestVersion})");
              Console.ResetColor();

              imagesToUpdate.Add((image.Id, image.OriginalName, image.OriginalTag, latestVersion, image.LocalDigest, string.Empty));
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

      // Perform the update on images.
      foreach (var image in imagesToUpdate)
      {
        if (options.Interactive)
        {
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

        // Find all containers using the image.
        var containersUsingImage = new List<(string Id, string Name, bool IsRunning, CreateContainerParameters CreateParameters)>();

        foreach (var containerId in dockerContainers.Where(c => c.ImageID == image.Id).Select(c => c.ID))
        {
          var inspect = await dockerClient.Containers.InspectContainerAsync(containerId);

          var containerConfig = new CreateContainerParameters
          {
            Image = $"{image.OriginalName}:{image.Tag}",
            Platform = inspect.Platform,
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

          containersUsingImage.Add((containerId, inspect.Name.TrimStart('/'), inspect.State.Running, containerConfig));
        }

        // Stop and remove containers using the image.
        foreach (var container in containersUsingImage)
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
              await dockerClient.Containers.RemoveContainerAsync(container.Id, new() { Force = true, RemoveLinks = false, RemoveVolumes = false, });
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

        try
        {
          Console.WriteLine($"Removing old image for {image.OriginalTag}");
          Console.WriteLine(image.LocalDigest);
          if (!options.DryRun)
          {
            await dockerClient.Images.DeleteImageAsync(image.Id, new() { Force = true, NoPrune = true });
          }

          Console.WriteLine($"Pulling new image for {image.OriginalName}:{image.Tag}");
          if (!string.IsNullOrEmpty(image.NewDigest))
          {
            Console.WriteLine(image.NewDigest);
          }

          if (!options.DryRun)
          {
            await dockerClient.Images.CreateImageAsync(new() { FromImage = image.OriginalName, Tag = image.Tag }, null, pullProgress);
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
        }

        // Restore and start containers using the new image.
        foreach (var container in containersUsingImage)
        {
          try
          {
            Console.WriteLine($"Restoring container {container.Id} ({container.Name})...");

            if (!options.DryRun)
            {
              var newContainer = await dockerClient.Containers.CreateContainerAsync(container.CreateParameters);
              if (container.IsRunning)
              {
                Console.WriteLine($"Starting new container {newContainer.ID}...");
                await dockerClient.Containers.StartContainerAsync(newContainer.ID, new());
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

        containersUsingImage.Clear();
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
