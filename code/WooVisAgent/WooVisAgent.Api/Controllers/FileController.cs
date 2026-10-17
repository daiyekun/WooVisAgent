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
    /// <summary>单张图片体积上限（10 MB）。再大只会拖慢模型调用，没有收益</summary>
    private const long MaxFileSize = 10L * 1024 * 1024;

    /// <summary>单次最多上传张数，与 ImageGenerationInputValidator 的参考图上限一致</summary>
    private const int MaxFileCount = 8;

    private static readonly HashSet<string> AllowedContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "image/png",
        "image/jpeg",
        "image/jpg",
        "image/webp",
        "image/gif",
        "image/bmp",
        "image/avif",
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
    };

    /// <summary>
    /// 上传参考图，一次可传多张（表单字段名任意，服务端按 <c>Request.Form.Files</c> 全收）。
    /// 前端调用方：WooVisAgent.Web/src/api/file.ts 的 uploadImages。
    /// </summary>
    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxFileSize * MaxFileCount)]
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
            return Result.Invalid($"一次最多上传 {MaxFileCount} 张图片。").ToActionResult();

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

    private static string? Validate(IFormFile file)
    {
        if (file.Length == 0)
            return $"文件「{file.FileName}」为空。";

        if (file.Length > MaxFileSize)
            return $"图片不能超过 {MaxFileSize / 1024 / 1024} MB。";

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
