using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexusUpdater;

internal static class Program
{
    private static readonly Regex VersionPattern = new("^[a-zA-Z0-9.-]+$", RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        try
        {
            var options = CommandLineOptions.Parse(args);
            var repositoryRoot = FindRepositoryRoot();
            var update = LoadPackage(repositoryRoot, options);
            PrintSummary(update.Package, options.DryRun);

            if (options.DryRun)
            {
                Console.WriteLine("检查通过；dry-run 未使用 API key，也未访问 Nexus Mods。");
                return 0;
            }

            ConfirmUpload(options.Yes);
            var apiKey = ReadApiKey(update.ConfigApiKey);
            using var client = new NexusClient(apiKey);
            var result = await client.PublishAsync(update.Package, cancellation.Token);
            var publishedKind = result.CreatedNewFile ? "文件" : "文件版本";
            Console.WriteLine(
                $"发布完成：Nexus {publishedKind} ID {result.PublishedId}，上传任务 ID {result.UploadId}。");
            return 0;
        }
        catch (HelpRequestedException)
        {
            PrintHelp();
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("操作已取消。");
            return 130;
        }
        catch (PartialPublishException exception)
        {
            Console.Error.WriteLine($"部分完成：{exception.Message}");
            Console.Error.WriteLine($"原因：{exception.InnerException?.Message}");
            return 2;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"失败：{exception.Message}");
            return 1;
        }
    }

    private static PreparedUpdate LoadPackage(string repositoryRoot, CommandLineOptions options)
    {
        ValidateDirectoryName(options.Mod);
        var modDirectory = Path.Combine(repositoryRoot, options.Mod);
        if (!Directory.Exists(modDirectory))
        {
            throw new DirectoryNotFoundException($"找不到模组目录：{modDirectory}");
        }

        var modInfoPath = Path.Combine(modDirectory, "modinfo.ini");
        if (!File.Exists(modInfoPath))
        {
            throw new FileNotFoundException("模组目录缺少 modinfo.ini。", modInfoPath);
        }

        var modInfo = ReadIni(modInfoPath);
        var modName = GetRequiredValue(modInfo, "name", modInfoPath);
        var version = GetRequiredValue(modInfo, "version", modInfoPath);
        var archiveName = $"{ToSafeFileNamePart(modName)}-{ToSafeFileNamePart(version)}.7z";
        var archivePath = Path.Combine(repositoryRoot, archiveName);
        ValidateArchive(modDirectory, archivePath);

        var configPath = options.ConfigFile is null
            ? Path.Combine(repositoryRoot, "Updater", "updater.json")
            : Path.GetFullPath(options.ConfigFile, Environment.CurrentDirectory);
        var config = LoadConfig(configPath);
        var target = FindTarget(config, options.Mod, modName);

        var changelogPath = Path.GetFullPath(options.ChangelogFile, Environment.CurrentDirectory);
        if (!File.Exists(changelogPath))
        {
            throw new FileNotFoundException("找不到 changelog 文件。", changelogPath);
        }

        var changelog = File.ReadAllText(changelogPath, Encoding.UTF8).Trim();
        if (changelog.Length == 0)
        {
            throw new InvalidOperationException("changelog 文件为空。");
        }

        ValidateReleaseMetadata(version, changelog, target);

        var package = new ModPackage(options.Mod, modName, version, archivePath, changelog, target);
        return new PreparedUpdate(package, config.NexusModsApiKey);
    }

    private static UpdaterConfig LoadConfig(string path)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "找不到 updater 配置。请复制 Updater/updater.example.json 为 Updater/updater.json 并填写已有 Nexus 模组的 ID。",
                path);
        }

        try
        {
            var config = JsonSerializer.Deserialize<UpdaterConfig>(
                File.ReadAllText(path, Encoding.UTF8),
                ConfigJsonOptions);
            if (config?.Mods is null)
            {
                throw new InvalidOperationException("updater 配置缺少 mods 对象。");
            }

            return config;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException($"updater 配置不是有效 JSON：{exception.Message}", exception);
        }
    }

    private static ModTarget FindTarget(UpdaterConfig config, string directoryName, string modName)
    {
        foreach (var entry in config.Mods)
        {
            if (entry.Key.Equals(directoryName, StringComparison.OrdinalIgnoreCase) ||
                entry.Key.Equals(modName, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Value
                    ?? throw new InvalidOperationException($"updater 配置中的 {entry.Key} 不能为空。");
            }
        }

        throw new InvalidOperationException(
            $"updater 配置中没有 {directoryName}（modinfo 名称：{modName}）的 Nexus 映射。");
    }

    private static void ValidateReleaseMetadata(
        string version,
        string changelog,
        ModTarget target)
    {
        ValidateConfiguredId(target.ModId, "modId");

        if (version.Length > 50 || !VersionPattern.IsMatch(version))
        {
            throw new InvalidOperationException(
                "版本号必须不超过 50 个字符，并且只能包含 ASCII 字母、数字、点和连字符。");
        }

        if (changelog.Length > 65_535)
        {
            throw new InvalidOperationException("changelog 不能超过 65535 个字符。");
        }
    }

    private static void ValidateConfiguredId(string value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Contains("replace", StringComparison.OrdinalIgnoreCase) ||
            value.Contains('<') ||
            value.Contains('>'))
        {
            throw new InvalidOperationException($"配置中的 {fieldName} 尚未填写有效值。");
        }
    }

    private static void ValidateArchive(string modDirectory, string archivePath)
    {
        var archive = new FileInfo(archivePath);
        if (!archive.Exists || archive.Length == 0)
        {
            throw new FileNotFoundException(
                $"找不到有效的打包文件 {archive.Name}；请先运行 .\\PackageMods.ps1。",
                archivePath);
        }

        var newestSourceWrite = Directory
            .EnumerateFiles(modDirectory, "*", SearchOption.AllDirectories)
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        if (archive.LastWriteTimeUtc < newestSourceWrite)
        {
            throw new InvalidOperationException(
                $"{archive.Name} 比模组源文件旧；请重新运行 .\\PackageMods.ps1 后再上传。");
        }
    }

    private static Dictionary<string, string> ReadIni(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            values[line[..separator].Trim()] = line[(separator + 1)..].Trim();
        }

        return values;
    }

    private static string GetRequiredValue(
        IReadOnlyDictionary<string, string> values,
        string key,
        string path)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidOperationException($"{path} 缺少 {key}。");
    }

    private static string ToSafeFileNamePart(string value)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var safeValue = new string(value.Select(character =>
            invalidCharacters.Contains(character) ? '_' : character).ToArray()).Trim().TrimEnd('.');

        return string.IsNullOrWhiteSpace(safeValue)
            ? throw new InvalidOperationException($"{value} 不能转换为有效文件名。")
            : safeValue;
    }

    private static void ValidateDirectoryName(string value)
    {
        if (!value.Equals(Path.GetFileName(value), StringComparison.Ordinal) || value is "." or "..")
        {
            throw new ArgumentException("--mod 只能指定仓库根目录下的单个模组目录名。");
        }
    }

    private static string FindRepositoryRoot()
    {
        foreach (var startPath in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(startPath); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "PackageMods.ps1")))
                {
                    return directory.FullName;
                }
            }
        }

        throw new DirectoryNotFoundException("找不到包含 PackageMods.ps1 的仓库根目录。");
    }

    private static void PrintSummary(ModPackage package, bool dryRun)
    {
        Console.WriteLine(dryRun ? "将检查以下更新：" : "即将发布以下更新：");
        Console.WriteLine($"  模组目录：{package.DirectoryName}");
        Console.WriteLine($"  模组名称：{package.Name}");
        Console.WriteLine($"  版本：    {package.Version}");
        Console.WriteLine($"  压缩包：  {Path.GetFileName(package.ArchivePath)}");
        Console.WriteLine($"  Nexus mod ID： {package.Target.ModId}");
        Console.WriteLine("  Nexus file：上传前自动解析；当前没有 Main File 时创建新的主要文件");
        Console.WriteLine($"  Changelog：{package.Changelog.Length} 个字符");
    }

    private static void ConfirmUpload(bool yes)
    {
        if (yes)
        {
            return;
        }

        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("非交互运行必须显式传入 --yes 才会上传。");
        }

        Console.Write("确认上传并更新 Nexus 页面？输入 yes 继续：");
        if (!string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
        {
            throw new OperationCanceledException();
        }
    }

    private static string ReadApiKey(string? configApiKey)
    {
        var apiKey = configApiKey?.Trim();
        return string.IsNullOrEmpty(apiKey)
            ? throw new InvalidOperationException(
                "updater 配置中的 NEXUSMODS_API_KEY 不能为空。")
            : apiKey;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            NexusUpdater - 将 PackageMods.ps1 生成的模组版本发布到 Nexus Mods

            用法：
              dotnet run --project Updater/NexusUpdater.csproj -- `
                --mod ShowHP `
                --changelog-file changelog.txt [--config Updater/updater.json] [--yes] [--dry-run]

            参数：
              --mod              仓库根目录下的模组目录名
              --changelog-file   UTF-8 changelog 文本文件
              --config           配置路径；默认 Updater/updater.json
              --yes              跳过上传前的交互确认，供自动化使用
              --dry-run          只校验配置、版本和压缩包，不使用密钥、不联网
              --help, -h         显示帮助

            API key 只从本地 updater.json 的 NEXUSMODS_API_KEY 字段读取。
            不支持通过环境变量、交互输入或命令行参数传入。
            """);
    }

    private sealed record PreparedUpdate(ModPackage Package, string? ConfigApiKey);
}
