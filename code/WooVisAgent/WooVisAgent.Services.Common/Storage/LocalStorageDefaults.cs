namespace WooVisAgent.Services.Common.Storage;

/// <summary>
/// 本地文件存储的路径约定。
///
/// 由 WooVisAgent.Infrastructure 的落盘实现与 WooVisAgent.Api 的静态托管共用，
/// 避免「写文件的目录」和「挂载对外访问的目录」两处各写死一个路径而写歪。
/// </summary>
public static class LocalStorageDefaults
{
    /// <summary>落盘根目录</summary>
    public const string RootPath = "D:\\";

    /// <summary>上传与产物的子目录</summary>
    public const string UploadFolder = "uploads";

    /// <summary>对外访问前缀，与 Api 中静态文件中间件的 RequestPath 一致</summary>
    public const string PublicRequestPath = "/uploads";

    /// <summary>
    /// 把存储层返回的相对路径（形如 <c>uploads/2026/10/18/xxx.mp4</c>）转成页面可直接使用的
    /// 对外地址（<c>/uploads/2026/10/18/xxx.mp4</c>）。
    ///
    /// 为什么必须带前导斜杠：不带斜杠时浏览器会按「相对当前页面路径」解析。
    /// 图片侧早就补了斜杠，视频侧漏了——于是同一个 <c>uploads/...</c> 在
    /// <c>/chat</c> 下碰巧能取到（被解析成 <c>/uploads/...</c>），
    /// 换到别的路由或子路径部署就会 404，表现为播放器一片黑。
    /// 已经是 http(s) 绝对地址的原样返回，避免二次拼接。
    /// </summary>
    public static string ToPublicUrl(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return string.Empty;

        var value = relativePath.Trim().Replace('\\', '/');

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute)
            && absolute.Scheme is "http" or "https")
        {
            return value;
        }

        var normalized = value.TrimStart('/');
        var uploadPrefix = $"{UploadFolder}/";

        if (normalized.StartsWith(uploadPrefix, StringComparison.OrdinalIgnoreCase))
            normalized = normalized[uploadPrefix.Length..];

        return $"{PublicRequestPath}/{normalized}";
    }

    /// <summary>
    /// 把对外访问地址（<c>/uploads/2026/10/17/xxx.png</c>）还原成存储层的相对路径
    /// （<c>uploads/2026/10/17/xxx.png</c>），供 <c>IFileStorageService</c> 直接读取。
    ///
    /// 任务的输入素材（参考图）与产物在库里存的都是对外地址，
    /// 而存储层只认相对路径，因此需要这一层换算——不写在这里就会出现
    /// 「Api 存的是 /uploads、Worker 拿去拼 D:\/uploads」的路径错乱。
    /// </summary>
    public static bool TryMapPublicUrlToRelativePath(string? url, out string relativePath)
    {
        relativePath = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
            return false;

        var value = url.Trim().Replace('\\', '/');

        if (Uri.TryCreate(value, UriKind.Absolute, out var absolute))
        {
            // 只有 http(s) 地址才可能指向本站的静态托管目录，其余（file、data 等）一律不接受
            if (absolute.Scheme is not ("http" or "https"))
                return false;

            value = absolute.AbsolutePath;
        }

        var normalized = value.TrimStart('/');

        // 只接受 uploads/ 前缀，避免把别的前缀误当成存储路径
        if (!normalized.StartsWith($"{UploadFolder}/", StringComparison.OrdinalIgnoreCase))
            return false;

        // 挡掉 ../ 形式的路径穿越
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment == "." || segment == ".."))
            return false;

        relativePath = string.Join('/', segments);
        return true;
    }
}
