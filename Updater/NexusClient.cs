using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;

namespace NexusUpdater;

internal sealed class NexusClient : IDisposable
{
    private const string ApiBaseUrl = "https://api.nexusmods.com/v3/";
    private const int MaximumErrorBodyLength = 4096;

    private static readonly HashSet<string> WritableFileCategories =
        new(["main", "optional", "miscellaneous"], StringComparer.Ordinal);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _apiKey;
    private readonly HttpClient _apiClient;
    private readonly HttpClient _storageClient;

    public NexusClient(string apiKey)
    {
        _apiKey = apiKey;
        _apiClient = new HttpClient(new HttpClientHandler
        {
            // Never forward the custom apikey header through an HTTP redirect.
            AllowAutoRedirect = false
        })
        {
            BaseAddress = new Uri(ApiBaseUrl),
            Timeout = TimeSpan.FromMinutes(5)
        };
        _apiClient.DefaultRequestHeaders.Add("apikey", apiKey);
        _apiClient.DefaultRequestHeaders.UserAgent.ParseAdd("MyOnimushaWotsMods-NexusUpdater/1.0");

        // This client deliberately has no Nexus API key. It is used only for
        // temporary storage URLs returned by Nexus Mods.
        _storageClient = new HttpClient
        {
            Timeout = TimeSpan.FromHours(1)
        };
    }

    public async Task<PublishResult> PublishAsync(ModPackage package, CancellationToken cancellationToken)
    {
        var target = await ResolveUploadTargetAsync(package, cancellationToken);
        if (target.CreatesNewFile)
        {
            Console.WriteLine(
                $"该 Nexus 模组当前没有 Main File，将创建新的主要文件，名称“{target.Name}”，分类 {target.Category}。");
        }
        else
        {
            Console.WriteLine(
                $"已自动选择 Nexus 文件 {target.FileId}，沿用名称“{target.Name}”和分类 {target.Category}。");
        }

        var archive = new FileInfo(package.ArchivePath);
        var upload = await CreateMultipartUploadAsync(archive, cancellationToken);
        Console.WriteLine($"已创建上传任务 {upload.Id}，共 {upload.PartUrls.Count} 个分片。");

        try
        {
            var parts = await UploadPartsAsync(archive, upload, cancellationToken);
            await CompleteMultipartUploadAsync(upload.CompleteUrl, parts, cancellationToken);
            await FinaliseUploadAsync(upload.Id, cancellationToken);
            await WaitUntilAvailableAsync(upload.Id, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new PartialPublishException(
                $"Nexus 文件尚未创建或更新，但上传任务 {upload.Id} 可能已经保留；请先检查 Nexus 后再重试。",
                exception);
        }

        string publishedId;
        try
        {
            publishedId = target.CreatesNewFile
                ? await CreateNewFileAsync(upload.Id, target, package, cancellationToken)
                : await CreateFileVersionAsync(upload.Id, target, package, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new PartialPublishException(
                $"压缩包已上传为任务 {upload.Id}，但创建 Nexus 文件或文件版本失败；请先检查 Nexus 后再重试。",
                exception);
        }

        try
        {
            await AddChangelogAsync(package.Target.ModId, package.Version, package.Changelog, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var publishedKind = target.CreatesNewFile ? "文件" : "文件版本";
            throw new PartialPublishException(
                $"{publishedKind} {publishedId} 已成功创建，但 changelog 更新失败。不要重复上传文件，只需在 Nexus 后台补写 changelog。",
                exception);
        }

        return new PublishResult(upload.Id, publishedId, target.CreatesNewFile);
    }

    public void Dispose()
    {
        _apiClient.Dispose();
        _storageClient.Dispose();
    }

    private async Task<NexusFileTarget> ResolveUploadTargetAsync(
        ModPackage package,
        CancellationToken cancellationToken)
    {
        var modId = package.Target.ModId;
        using var document = await SendApiAsync(
            HttpMethod.Get,
            $"mods/{Uri.EscapeDataString(modId)}/files",
            body: null,
            HttpStatusCode.OK,
            cancellationToken);

        var data = GetRequiredProperty(document.RootElement, "data");
        var files = GetRequiredProperty(data, "mod_files");
        if (files.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Nexus 响应字段 mod_files 不是数组。");
        }

        var fileStates = new List<NexusFileState>();
        foreach (var file in files.EnumerateArray())
        {
            var fileId = GetRequiredText(file, "id");
            var versions = await GetFileVersionsAsync(fileId, cancellationToken);
            fileStates.Add(new NexusFileState(
                fileId,
                GetRequiredBoolean(file, "is_active"),
                versions));
        }

        if (fileStates
            .SelectMany(file => file.Versions)
            .Any(version => version.Version.Equals(package.Version, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"版本 {package.Version} 已在目标 Nexus 模组中使用；每次上传必须使用不同版本号。");
        }

        var mainFiles = fileStates
            .Where(file => file.IsActive && file.Versions.Any(version =>
                version.Category.Equals("main", StringComparison.Ordinal)))
            .ToArray();

        if (mainFiles.Length == 0)
        {
            return CreateNewFileTarget(package);
        }

        if (mainFiles.Length > 1)
        {
            throw new InvalidOperationException(
                $"该 Nexus 模组有 {mainFiles.Length} 个当前 Main File；为避免更新错误文件，已在上传前终止。");
        }

        var mainFile = mainFiles[0];
        var primaryMainVersions = mainFile.Versions
            .Where(version => version.IsPrimary &&
                version.Category.Equals("main", StringComparison.Ordinal))
            .ToArray();
        if (primaryMainVersions.Length != 1)
        {
            throw new InvalidOperationException(
                $"当前 Main File 中应恰好有一个主要版本，实际找到 {primaryMainVersions.Length} 个；已在上传前终止。");
        }

        var primaryVersion = primaryMainVersions[0];
        var target = new NexusFileTarget(
            mainFile.Id,
            primaryVersion.Name,
            primaryVersion.Category,
            CreatesNewFile: false);
        ValidateUploadTarget(target);
        return target;
    }

    private async Task<IReadOnlyList<NexusFileVersion>> GetFileVersionsAsync(
        string fileId,
        CancellationToken cancellationToken)
    {
        using var document = await SendApiAsync(
            HttpMethod.Get,
            $"mod-files/{Uri.EscapeDataString(fileId)}/versions",
            body: null,
            HttpStatusCode.OK,
            cancellationToken);

        var data = GetRequiredProperty(document.RootElement, "data");
        var versions = GetRequiredProperty(data, "versions");
        if (versions.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Nexus 响应字段 versions 不是数组。");
        }

        return versions
            .EnumerateArray()
            .Select(version => new NexusFileVersion(
                GetRequiredText(version, "name"),
                GetRequiredText(version, "version"),
                GetRequiredText(version, "category"),
                GetOptionalBoolean(version, "is_primary")))
            .ToArray();
    }

    private static NexusFileTarget CreateNewFileTarget(ModPackage package)
    {
        var target = new NexusFileTarget(
            FileId: null,
            package.Name,
            Category: "main",
            CreatesNewFile: true);
        ValidateUploadTarget(target);
        return target;
    }

    private static void ValidateUploadTarget(NexusFileTarget target)
    {
        if (target.Name.Length > 50 || target.Name.Any(character =>
            !((character >= 'a' && character <= 'z') ||
              (character >= 'A' && character <= 'Z') ||
              (character >= '0' && character <= '9') ||
              character is ' ' or '_' or '\'' or '(' or ')' or '.' or '-')))
        {
            var source = target.CreatesNewFile ? "modinfo.ini 中的模组名称" : "当前文件显示名称";
            throw new InvalidOperationException(
                $"{source}不符合 Nexus 文件接口的要求；工具不会修改或截断它，已在上传前终止。");
        }

        if (!WritableFileCategories.Contains(target.Category))
        {
            throw new InvalidOperationException(
                $"文件分类 {target.Category} 无法用于发布；工具不会替换分类，已在上传前终止。");
        }
    }

    private async Task<MultipartUpload> CreateMultipartUploadAsync(
        FileInfo archive,
        CancellationToken cancellationToken)
    {
        using var document = await SendApiAsync(
            HttpMethod.Post,
            "uploads/multipart",
            new
            {
                filename = archive.Name,
                size_bytes = archive.Length.ToString(CultureInfo.InvariantCulture)
            },
            HttpStatusCode.Created,
            cancellationToken);

        var data = GetRequiredProperty(document.RootElement, "data");
        var id = GetRequiredText(data, "id");
        var partSize = GetRequiredProperty(data, "part_size_bytes").GetInt64();
        var completeUrl = GetRequiredHttpsUrl(data, "complete_presigned_url");
        var partUrls = GetRequiredProperty(data, "part_presigned_urls")
            .EnumerateArray()
            .Select(ReadHttpsUrl)
            .ToArray();

        if (partSize is <= 0 or > int.MaxValue || partUrls.Length == 0)
        {
            throw new InvalidOperationException("Nexus 返回了无效的分片参数。");
        }

        var expectedParts = (archive.Length + partSize - 1) / partSize;
        if (expectedParts != partUrls.Length)
        {
            throw new InvalidOperationException(
                $"Nexus 返回的分片数量不一致：预期 {expectedParts}，实际 {partUrls.Length}。");
        }

        return new MultipartUpload(id, partUrls, (int)partSize, completeUrl);
    }

    private async Task<IReadOnlyList<UploadedPart>> UploadPartsAsync(
        FileInfo archive,
        MultipartUpload upload,
        CancellationToken cancellationToken)
    {
        var results = new List<UploadedPart>(upload.PartUrls.Count);
        await using var stream = new FileStream(
            archive.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        for (var index = 0; index < upload.PartUrls.Count; index++)
        {
            var remaining = archive.Length - stream.Position;
            var byteCount = (int)Math.Min(upload.PartSize, remaining);
            var buffer = new byte[byteCount];
            await ReadExactlyAsync(stream, buffer, cancellationToken);

            Console.WriteLine($"正在上传分片 {index + 1}/{upload.PartUrls.Count}（{byteCount} bytes）……");
            using var content = new ByteArrayContent(buffer);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = byteCount;

            HttpResponseMessage response;
            try
            {
                response = await _storageClient.PutAsync(upload.PartUrls[index], content, cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException($"上传分片 {index + 1} 超时；上传地址已从错误信息中隐藏。");
            }
            catch (HttpRequestException exception)
            {
                throw new HttpRequestException(
                    $"上传分片 {index + 1} 时发生网络错误；上传地址已从错误信息中隐藏。",
                    null,
                    exception.StatusCode);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException(
                        $"上传分片 {index + 1} 失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}。",
                        null,
                        response.StatusCode);
                }

                var etag = response.Headers.ETag?.Tag;
                if (string.IsNullOrWhiteSpace(etag) && response.Headers.TryGetValues("ETag", out var values))
                {
                    etag = values.FirstOrDefault();
                }

                if (string.IsNullOrWhiteSpace(etag))
                {
                    throw new InvalidOperationException($"上传分片 {index + 1} 后没有收到 ETag。");
                }

                results.Add(new UploadedPart(index + 1, etag.Trim('"')));
            }
        }

        return results;
    }

    private async Task CompleteMultipartUploadAsync(
        Uri completeUrl,
        IReadOnlyList<UploadedPart> parts,
        CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        using (var writer = XmlWriter.Create(builder, new XmlWriterSettings
        {
            OmitXmlDeclaration = true,
            Indent = true
        }))
        {
            writer.WriteStartElement("CompleteMultipartUpload");
            foreach (var part in parts)
            {
                writer.WriteStartElement("Part");
                writer.WriteElementString("PartNumber", part.PartNumber.ToString(CultureInfo.InvariantCulture));
                writer.WriteElementString("ETag", part.ETag);
                writer.WriteEndElement();
            }

            writer.WriteEndElement();
        }

        using var content = new StringContent(builder.ToString(), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/xml");
        HttpResponseMessage response;
        try
        {
            response = await _storageClient.PostAsync(completeUrl, content, cancellationToken);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("合并上传分片超时；上传地址已从错误信息中隐藏。");
        }
        catch (HttpRequestException exception)
        {
            throw new HttpRequestException(
                "合并上传分片时发生网络错误；上传地址已从错误信息中隐藏。",
                null,
                exception.StatusCode);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new HttpRequestException(
                    $"合并上传分片失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}。",
                    null,
                    response.StatusCode);
            }
        }
    }

    private async Task FinaliseUploadAsync(string uploadId, CancellationToken cancellationToken)
    {
        using var document = await SendApiAsync(
            HttpMethod.Post,
            $"uploads/{Uri.EscapeDataString(uploadId)}/finalise",
            body: null,
            expectedStatusCode: null,
            cancellationToken);
    }

    private async Task WaitUntilAvailableAsync(string uploadId, CancellationToken cancellationToken)
    {
        const int maximumAttempts = 60;
        var delay = TimeSpan.FromSeconds(2);

        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            using var document = await SendApiAsync(
                HttpMethod.Get,
                $"uploads/{Uri.EscapeDataString(uploadId)}",
                body: null,
                expectedStatusCode: null,
                cancellationToken);

            var state = GetRequiredText(GetRequiredProperty(document.RootElement, "data"), "state");
            Console.WriteLine($"Nexus 正在处理上传：{state}");
            if (state.Equals("available", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (state is "failed" or "error" or "cancelled")
            {
                throw new InvalidOperationException($"Nexus 上传任务进入终止状态：{state}。");
            }

            if (attempt < maximumAttempts)
            {
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 1.5, 30_000));
            }
        }

        throw new TimeoutException($"等待 Nexus 处理上传任务 {uploadId} 超时。");
    }

    private async Task<string> CreateFileVersionAsync(
        string uploadId,
        NexusFileTarget target,
        ModPackage package,
        CancellationToken cancellationToken)
    {
        using var document = await SendApiAsync(
            HttpMethod.Post,
            $"mod-files/{Uri.EscapeDataString(target.FileId!)}/versions",
            new
            {
                upload_id = uploadId,
                name = target.Name,
                description = string.IsNullOrWhiteSpace(package.Target.Description)
                    ? null
                    : package.Target.Description,
                version = package.Version,
                file_category = target.Category,
                primary_mod_manager_download = true,
                update_mod_version = true
            },
            HttpStatusCode.Created,
            cancellationToken);

        var data = GetRequiredProperty(document.RootElement, "data");
        return GetRequiredText(GetRequiredProperty(data, "version"), "id");
    }

    private async Task<string> CreateNewFileAsync(
        string uploadId,
        NexusFileTarget target,
        ModPackage package,
        CancellationToken cancellationToken)
    {
        using var document = await SendApiAsync(
            HttpMethod.Post,
            "mod-files",
            new
            {
                upload_id = uploadId,
                mod_id = package.Target.ModId,
                name = target.Name,
                description = string.IsNullOrWhiteSpace(package.Target.Description)
                    ? null
                    : package.Target.Description,
                version = package.Version,
                file_category = target.Category,
                primary_mod_manager_download = true,
                update_mod_version = true
            },
            HttpStatusCode.Created,
            cancellationToken);

        var data = GetRequiredProperty(document.RootElement, "data");
        return GetRequiredText(data, "id");
    }

    private async Task AddChangelogAsync(
        string modId,
        string version,
        string changelog,
        CancellationToken cancellationToken)
    {
        using var document = await SendApiAsync(
            HttpMethod.Post,
            $"mods/{Uri.EscapeDataString(modId)}/changelogs",
            new { version, changelog },
            HttpStatusCode.Created,
            cancellationToken);
    }

    private async Task<JsonDocument> SendApiAsync(
        HttpMethod method,
        string relativeUrl,
        object? body,
        HttpStatusCode? expectedStatusCode,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUrl);
        if (body is not null)
        {
            request.Content = new StringContent(
                JsonSerializer.Serialize(body, JsonOptions),
                Encoding.UTF8,
                "application/json");
        }

        using var response = await _apiClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode || expectedStatusCode is not null && response.StatusCode != expectedStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (responseBody.Length > MaximumErrorBodyLength)
            {
                responseBody = responseBody[..MaximumErrorBodyLength] + "…";
            }

            responseBody = responseBody.Replace(_apiKey, "[REDACTED]", StringComparison.Ordinal);
            throw new HttpRequestException(
                $"Nexus API {method} /{relativeUrl} 失败：HTTP {(int)response.StatusCode} {response.ReasonPhrase}。{Environment.NewLine}{responseBody}",
                null,
                response.StatusCode);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        if (stream == Stream.Null || response.Content.Headers.ContentLength == 0)
        {
            return JsonDocument.Parse("{}");
        }

        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static async Task ReadExactlyAsync(
        Stream stream,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var totalRead = 0;
        while (totalRead < buffer.Length)
        {
            var bytesRead = await stream.ReadAsync(buffer[totalRead..], cancellationToken);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException("读取待上传压缩包时提前到达文件末尾。");
            }

            totalRead += bytesRead;
        }
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidOperationException($"Nexus 响应缺少字段 {propertyName}。");
        }

        return property;
    }

    private static string GetRequiredText(JsonElement element, string propertyName)
    {
        var property = GetRequiredProperty(element, propertyName);
        var value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };

        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"Nexus 响应字段 {propertyName} 为空或类型无效。")
            : value;
    }

    private static bool GetRequiredBoolean(JsonElement element, string propertyName)
    {
        var property = GetRequiredProperty(element, propertyName);
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException(
                $"Nexus 响应字段 {propertyName} 不是布尔值。")
        };
    }

    private static bool GetOptionalBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidOperationException(
                $"Nexus 响应字段 {propertyName} 不是布尔值。")
        };
    }

    private static Uri GetRequiredHttpsUrl(JsonElement element, string propertyName) =>
        ReadHttpsUrl(GetRequiredProperty(element, propertyName));

    private static Uri ReadHttpsUrl(JsonElement element)
    {
        var value = element.GetString();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Nexus 返回了非 HTTPS 的上传地址，已拒绝继续。");
        }

        return uri;
    }

    private sealed record MultipartUpload(
        string Id,
        IReadOnlyList<Uri> PartUrls,
        int PartSize,
        Uri CompleteUrl);

    private sealed record UploadedPart(int PartNumber, string ETag);

    private sealed record NexusFileState(
        string Id,
        bool IsActive,
        IReadOnlyList<NexusFileVersion> Versions);

    private sealed record NexusFileVersion(
        string Name,
        string Version,
        string Category,
        bool IsPrimary);

    private sealed record NexusFileTarget(
        string? FileId,
        string Name,
        string Category,
        bool CreatesNewFile);
}
