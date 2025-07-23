namespace ContainerUpdater.Models;

public sealed record ContainerUpdateGroup(
    string ImageId,
    string ImageName,
    string NewTag,
    List<ContainerInfo> Containers);
