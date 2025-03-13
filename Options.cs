using CommandLine;

namespace ContainerUpdater;

public class Options
{
  [Option('e', "exclude", Required = false, HelpText = "Exclude any images where the name, repository or both matches values in the list.")]
  public IEnumerable<string> Exclude { get; set; } = [];

  [Option('i', "include", Required = false, HelpText = "Include only images where the name, repository or both matches values in the list. Excluded values take precedence if used.")]
  public IEnumerable<string> Include { get; set; } = [];

  [Option("interactive", Required = false, HelpText = "Pause and choose which images to update in interactive mode.")]
  public bool Interactive { get; set; }

  [Option("dry-run", Required = false, HelpText = "Check for updates and log what would happen but do not make any changes.")]
  public bool DryRun { get; set; }
}
