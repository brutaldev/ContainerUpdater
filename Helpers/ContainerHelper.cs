using ContainerUpdater.Models;

namespace ContainerUpdater.Helpers;

public static class ContainerHelper
{
  /// <summary>
  /// Sorts containers in dependency order (dependencies before dependents) using DFS topological sort.
  /// Used to determine the correct stop order (dependencies last) and start order (dependencies first).
  /// </summary>
  public static List<ContainerInfo> SortByDependencies(List<ContainerInfo> containers)
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
}
