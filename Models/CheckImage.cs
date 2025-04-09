namespace ContainerUpdater.Models;

public sealed record CheckImage(
  string Id,
  string OriginalName,
  string OriginalTag,
  string Registry,
  string Repository,
  string Tag,
  string LocalDigest);
