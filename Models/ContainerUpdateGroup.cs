namespace ContainerUpdater.Models;

public sealed record ContainerUpdateGroup(
    UpdateImage Image,
    List<ContainerInfo> Containers);
