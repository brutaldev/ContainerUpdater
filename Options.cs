using CommandLine;

namespace ContainerUpdater;

public class Options
{
  [Option("dry-run", Required = false, HelpText = "Check for updates and log what would happen but do not make any changes.")]
  public bool DryRun { get; set; }
}
