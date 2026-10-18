using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using WooVisAgent.Api.Extensions;
using WooVisAgent.Services.Common.Contracts;
using WooVisAgent.Services.Common.Storage;
using WooVisAgent.SharedLib.Ids;
using WooVisAgent.SharedLib.Result;

namespace WooVisAgent.Api.Controllers;

/// <summary>
/// 素材上传接口，供「图生图」的参考图使用。
///
/// 与图片生成用例分开放在这里的原因：上传只是「把字节落盘并换回一个可访问地址」，
/// 不涉及领域逻辑，无需走 MediatR 用例层；
/// 落盘实现（<see cref="IFileStorageService" />）本来就由 Api 宿主注册，
/// 与 GenerateWorker 共用同一目录，因此 Worker 后续能按同一路径把参考图读回来。
///
/// 返回的地址形如 <c>/uploads/2026/10/17/xxx.png</c>，由 Api 的静态文件中间件托管，
/// 可直接作为 <c>ImageGenerationInput.ReferenceImageUrls</c> 提交。
/// </summary>
[ApiController]
[Route("api/files")]
[Authorize]
[Produces("application/json")]
public sealed class FileController(
    IFileStorageService fileStorage,
    ISnowflakeIdGenerator idGenerator,
    ILogger<FileController> logger) : ControllerBase
{
    /// <summary>
    /// 单个文件体积上限。图片给到 10MB 就够，但视频 / 音频参考动辄几十 MB，
    /// 沿用 10MB 会让用户传参考视频时被服务端直接拒掉，且报错只说"文件过大"，
    /// 完全看不出是"图片上限"套到了视频身上。
    /// </summary>
    private const long MaxImageSize = 10L * 1024 * 1024;
    private const long MaxFileSize = 100L * 1024 * 1024;

    /// <summary>单次请求体总上限（一次传 50 个素材的现实值）</summary>
    private const long MaxRequestSize = 512L * 1024 * 1024;

    /// <summary>单次最多上传个数，与 VideoGenerationInputValidator.MaxReferences 对齐</summary>
    private const int MaxFileCount = 50;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/jpg",
        "image/webp",
        "image/gif",
        "image/bmp",
        "image/avif",

        // 视频参考（火山方舟全能生成用来复刻运镜与动作）
        "video/mp4",
        "video/quicktime",
        "video/x-m4v",
        "video/webm",

        // 音频参考（用来参考音色）
        "audio/mpeg",
        "audio/mp3",
        "audio/wav",
        "audio/x-wav",
        "audio/mp4",
        "audio/x-m4a",
        "audio/aac",
        "audio/ogg",
    };

    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp",
        ".gif",
        ".bmp",
        ".avif",

        ".mp4",
        ".mov",
        ".m4v",
        ".webm",

        ".mp3",
        ".wav",
        ".m4a",
        ".aac",
        ".ogg",
    };

    /// <summary>
    /// 上传参考素材（图片 / 视频 / 音频），一次可传多个（表单字段名任意，服务端按 <c>Request.Form.Files</c> 全收）。
    /// 前端调用方：WooVisAgent.Web/src/api/file.ts 的 uploadFiles。
    /// </summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxRequestSize)]
    [ProducesResponseType<IReadOnlyList<UploadFileResultDto>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Upload(CancellationToken cancellationToken)
    {
        if (!Request.HasFormContentType)
            return Result.Invalid("请以 multipart/form-data 提交文件。").ToActionResult();

        var form = await Request.ReadFormAsync(cancellationToken);

        if (form.Files.Count == 0)
            return Result.Invalid("没有收到任何文件。").ToActionResult();

        if (form.Files.Count > MaxFileCount)
            return Result.Invalid($"一次最多上传 {MaxFileCount} 个素材。").ToActionResult();

        var results = new List<UploadFileResultDto>(form.Files.Count);

        foreach (var file in form.Files)
        {
            var error = Validate(file);
            if (error is not null)
                return Result.Invalid(error).ToActionResult();

            // 文件名只用于落盘与回显，去掉目录成分，避免外部传入带路径的名字
            var safeFileName = Path.GetFileName(file.FileName);
            if (string.IsNullOrWhiteSpace(safeFileName))
                safeFileName = $"reference.{GuessExtension(file.ContentType)}";

            await using var stream = file.OpenReadStream();

            var relativePath = await fileStorage.SaveAsync(stream, safeFileName, cancellationToken);
            var publicUrl = BuildPublicUrl(relativePath);

            results.Add(new UploadFileResultDto(
                idGenerator.NextId().ToString(CultureInfo.InvariantCulture),
                publicUrl,
                safeFileName,
                file.Length,
                file.ContentType));

            logger.LogInformation("参考图已上传：{FileName}（{Size} 字节）→ {Url}", safeFileName, file.Length, publicUrl);
        }

        return Result.Success<IReadOnlyList<UploadFileResultDto>>(results).ToActionResult();
    }

    private static bool IsImageExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp" or ".avif" => true,
        _ => false,
    };

    private static string? Validate(IFormFile file)
    {
        if (file.Length == 0)
            return $"文件「{file.FileName}」为空。";

        var isImage = file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                      || IsImageExtension(Path.GetExtension(file.FileName));

        var limit = isImage ? MaxImageSize : MaxFileSize;
        if (file.Length > limit)
            return $"{(isImage ? "图片" : "素材")}不能超过 {limit / 1024 / 1024} MB。";

        // 扩展名与 Content-Type 只要有一个可信就放行：
        // 浏览器一般两者都给，但从剪贴板粘贴时 Content-Type 可能是空串，
        // 只认 Content-Type 会误杀，只认扩展名又太松，这里取"或"的关系。
        var extension = Path.GetExtension(file.FileName);
        var contentTypeOk = !string.IsNullOrWhiteSpace(file.ContentType)
                            && AllowedContentTypes.Contains(file.ContentType);
        var extensionOk = AllowedExtensions.Contains(extension);

        if (!contentTypeOk && !extensionOk)
            return "只支持上传图片（PNG / JPG / WebP / GIF / BMP / AVIF）。";

        return null;
    }

    private static string GuessExtension(string? contentType) => (contentType ?? string.Empty).ToLowerInvariant() switch
    {
        "image/jpeg" or "image/jpg" => "jpg",
        "image/webp" => "webp",
        "image/gif" => "gif",
        "image/bmp" => "bmp",
        "image/avif" => "avif",
        _ => "png",
    };

    /// <summary>
    /// 把存储层返回的相对路径（<c>uploads/2026/10/17/xxx.png</c>）
    /// 转成页面可直接使用的访问地址（<c>/uploads/2026/10/17/xxx.png</c>）。
    /// 与图片产物的做法保持一致，避免参考图与产物出现两套地址风格。
    /// </summary>
    private static string BuildPublicUrl(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var uploadPrefix = $"{LocalStorageDefaults.UploadFolder}/";

        if (normalized.StartsWith(uploadPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[uploadPrefix.Length..];

        return $"{LocalStorageDefaults.PublicRequestPath}/{normalized}";
    }
}

/// <summary>上传结果，字段与前端 src/api/types.ts 的 UploadResult 对齐</summary>
public sealed record UploadFileResultDto(
    string Id,
    string Url,
    string FileName,
    long FileSize,
    string? ContentType);
