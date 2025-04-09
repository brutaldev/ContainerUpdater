namespace ContainerUpdater.Models;

public sealed record UpdateImage(
  string Id,
  string OriginalName,
  string OriginalTag,
  string Tag,
  string LocalDigest,
  string NewDigest);
