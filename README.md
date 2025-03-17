![ICON](https://raw.githubusercontent.com/brutaldev/ContainerUpdater/main/icon_small.png)
	
# Container Updater

Automate updating Docker images and the containers that use them.

Updating Docker images in-place is a surprisingly complex task that requires multiple steps which are both time consuming and error prone if done manually.
Container Updater completely automates this process in the simplest way possible (just run it).

Container Updater is available as a .NET Core Global Tool:

```bash
dotnet tool install --global ContainerUpdater
```

The latest version can also be downloaded directly from NuGet.org at:
https://www.nuget.org/packages/ContainerUpdater

If you don't have .NET installed you can download the latest version for your operating system here:
https://github.com/brutaldev/ContainerUpdater/releases/latest

![SCREENSHOT](https://raw.githubusercontent.com/brutaldev/ContainerUpdater/main/screenshot.png)

### Options

#### Dry Run
If you want to see if there are any updates and what will happen but don't want to make any changes you can use the `--dry-run` option.

```bash
ContainerUpdater --dry-run
```

#### Interactive Mode
If you want to pause at certain steps and choose which images to update you can use the `--interactive` mode option.

```bash
ContainerUpdater --interactive
```

#### Include / Exclude
If you want to include or exclude certain repository names from update checks, you can pass them in as a lists using the organization, image name or full name.
Excluding takes precedence over include matches.

```bash
# Will exclude images from deventerprisesoftware (https://hub.docker.com/u/deventerprisesoftware) and microsoft/garnet.
ContainerUpdater --exclude deventerprisesoftware garnet

# Include full repository names as well.
ContainerUpdater --include deventerprisesoftware/html2pdf

# Include only images from Microsoft (https://hub.docker.com/u/microsoft).
ContainerUpdater --include microsoft
```

#### Remote Host
Instead of connecting to a local Docker instance, you can connect to a remote host instead using the `--host` option. This needs to be a valid URI.

```bash
ContainerUpdater --host tcp://127.0.0.1:2375
```

#### Credentials
If your Docker instance (local or remote) requires credentials then you can supply those with the `--username` and `--password` options.

```bash
ContainerUpdater --username admin --password secret_sauce
```

### How It Works

1. Get all the current image digests and tags to perform a manifest lookup.
2. Lookup latest manifest and check if it matches the current image digest.
3. If not the latest, get the containers that are using the old/existing image and stop them.
4. Inspect and retain the information to re-install containers.
5. Remove the containers using the old image.
6. Remove the old image.
7. Pull the new image.
8. Re-create the containers from previous inspect data.
9. Start the containers if they were previously running.

### Alternatives

Watchtower (https://github.com/containrrr/watchtower) and Ouroboros (https://github.com/pyouroboros/ouroboros) are both alternatives that perform the same in-place update.
Both these options run as docker containers themselves which actually creates unnecessary complexity.
Container Update was created because these options just take too long to setup effectively as well as requiring their own maintenance.

Running an updater outside of Docker is incredibly simple and requires zero setup.
Container Updater also provides the following:
- No installation or configuration, just double-click.
- Works on all operating systems regardless of Docker environment.
- Works with all repositories and manifest versions.
- Improved/reliable update checks using multiple digest lookups.
- Automatically use cached authentication on all operating systems including credential helpers.
- Full restore of containers including all labels and annotations.

### TODO

- [x] Automatically use available cached credentials (cross-platform)
- [x] Handle automatic token generation for different registries
- [x] Handle multiple digest checks using different content types
- [x] Restore all attributes as well (compose groups)
- [x] Support dry run just to check for and show updates
- [x] Support adding image names to include/exclude in checks
- [x] Support selection of images to update (interactive mode)
- [x] Support updating a remote docker host
- [ ] Export container settings to recover from failures
- [ ] Add cross-platform UI to run in the system tray
- [x] Deploy as a .NET global tool
- [x] Write all output to log file
- [x] Detect version numbers and new version tags
