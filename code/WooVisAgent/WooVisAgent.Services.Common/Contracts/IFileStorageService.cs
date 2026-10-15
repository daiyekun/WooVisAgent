using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Zilor.AICopilot.Services.Common.Contracts;

public interface IFileStorageService
{
    /// <summary>
    /// 保存文件
    /// </summary>
    /// <param name="stream">文件流</param>
    /// <param name="fileName">文件名</param>
    /// <param name="cancellationToken"></param>
    Task<string> SaveAsync(Stream stream, string fileName, CancellationToken cancellationToken = default);
    /// <returns>返回相对存储路径或URL</returns>

    /// <summary>
    /// 获取文件流
    /// </summary>
    /// <param name="path">文件路径</param>
    /// <returns></returns>
    Task<Stream?> GetAsync(string path, CancellationToken cancellationToken = default);

    /// <summary>
    /// 删除文件
    /// </summary>
    /// <param name="path"></param>
    /// <returns></returns>
    Task DeleteAsync(string path, CancellationToken cancellationToken = default);
}