using System.Text.Json.Serialization;

namespace NexusUpdater;

internal sealed class UpdaterConfig
{
    [JsonPropertyName("NEXUSMODS_API_KEY")]
    public string? NexusModsApiKey { get; init; }

    public Dictionary<string, ModTarget> Mods { get; init; } = [];
}

internal sealed class ModTarget
{
    public string ModId { get; init; } = "";
    public string? DisplayName { get; init; }
    public string? Description { get; init; }
    public string FileCategory { get; init; } = "main";
    public bool ArchiveExistingVersion { get; init; }
    public bool? PrimaryModManagerDownload { get; init; }
    public bool? AllowModManagerDownload { get; init; }
    public bool? ShowRequirementsPopUp { get; init; }
}

internal sealed record ModPackage(
    string DirectoryName,
    string Name,
    string Version,
    string ArchivePath,
    string Changelog,
    ModTarget Target);

internal sealed record PublishResult(string UploadId, string VersionId);

internal sealed record CommandLineOptions(
    string Mod,
    string ChangelogFile,
    string? ConfigFile,
    bool Yes,
    bool DryRun)
{
    public static CommandLineOptions Parse(string[] args)
    {
        string? mod = null;
        string? changelogFile = null;
        string? configFile = null;
        var yes = false;
        var dryRun = false;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--mod":
                    mod = ReadValue(args, ref index, "--mod");
                    break;
                case "--changelog-file":
                    changelogFile = ReadValue(args, ref index, "--changelog-file");
                    break;
                case "--config":
                    configFile = ReadValue(args, ref index, "--config");
                    break;
                case "--yes":
                    yes = true;
                    break;
                case "--dry-run":
                    dryRun = true;
                    break;
                case "--help":
                case "-h":
                    throw new HelpRequestedException();
                default:
                    throw new ArgumentException($"未知参数：{args[index]}");
            }
        }

        if (string.IsNullOrWhiteSpace(mod))
        {
            throw new ArgumentException("缺少必需参数 --mod。");
        }

        if (string.IsNullOrWhiteSpace(changelogFile))
        {
            throw new ArgumentException("缺少必需参数 --changelog-file。");
        }

        return new CommandLineOptions(mod, changelogFile, configFile, yes, dryRun);
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException($"{option} 缺少参数值。");
        }

        return args[index];
    }
}

internal sealed class HelpRequestedException : Exception;

internal sealed class PartialPublishException(string message, Exception innerException)
    : Exception(message, innerException);
