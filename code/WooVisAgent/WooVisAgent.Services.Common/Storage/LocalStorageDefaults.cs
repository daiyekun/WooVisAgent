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
}
