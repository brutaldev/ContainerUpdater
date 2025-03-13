using CommandLine;

namespace ContainerUpdater;

public class Options
{
  [Option('i', "interactive", Required = false, HelpText = "Pause and choose which images to update in interactive mode.")]
  public bool Interactive { get; set; }

  [Option("dry-run", Required = false, HelpText = "Check for updates and log what would happen but do not make any changes.")]
  public bool DryRun { get; set; }
}
