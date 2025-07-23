using Docker.DotNet.Models;

namespace ContainerUpdater.Models;

public sealed record ContainerInfo(
    string Id,
    string Name,
    bool IsRunning,
    CreateContainerParameters CreateParameters,
    HashSet<string> Dependencies,
    string Project);
