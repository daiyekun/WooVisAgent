using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace WooVisAgentServices.VideoService.Support;

/// <summary>
/// ffmpeg / ffprobe 的路径与超时配置。
///
/// 配置来源按优先级：<c>appsettings.json</c> 的 <c>VideoMedia</c> 节点 ← 环境变量
/// <c>VideoMedia__FfmpegPath</c>（Aspire / Docker 部署时最方便）。
/// </summary>
public sealed class VideoMediaOptions
{
    /// <summary>
    /// ffmpeg 可执行文件路径。
    ///
    /// <para>
    /// 支持两种写法：
    /// <list type="bullet">
    ///   <item><b>完整路径</b>（推荐）：如 <c>D:\tools\ffmpeg\bin\ffmpeg.exe</c>。
    ///         服务器与容器镜像里 ffmpeg 常常不在 PATH 上，这是唯一可靠的方式。</item>
    ///   <item><b>裸命令名</b>（默认 <c>ffmpeg</c>）：交给系统 PATH 查找。</item>
    /// </list>
    /// </para>
    ///
    /// 无论哪种写法，启动时都会<b>真的执行一次 <c>-version</c> 来确认可用</b>。
    /// 探测不到时视频仍会正常生成，只是不带封面、时长与分辨率（降级而非报错）。
    /// </summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>
    /// ffprobe 可执行文件路径。
    ///
    /// 留空时会先尝试「与 <see cref="FfmpegPath" /> 同目录的 ffprobe」，
    /// 找不到再回退到系统 PATH。
    /// <para>
    /// 多数视频元信息（时长、分辨率）都能用 ffprobe 读到，
    /// 而封面只用 ffmpeg——所以只配 ffmpeg 时，封面能出，时长可能仍为空。
    /// 想两个都要，就显式把 ffprobe 的路径也配上。
    /// </para>
    /// </summary>
    public string? FfprobePath { get; set; }

    /// <summary>单次探测 / 抽帧的超时上限，防止异常文件把 Worker 卡死</summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>抽帧的时间点（秒）。取 1 秒而不是 0，是因为很多视频第 0 帧是纯黑场。</summary>
    public double ThumbnailAtSecond { get; set; } = 1;
}

/// <summary>
/// 基于 ffmpeg / ffprobe 的 <see cref="IVideoMediaInspector" /> 实现。
///
/// <b>可用性探测只做一次</b>：ffmpeg 缺失是环境问题而非任务问题，
/// 每条任务都去探测一次会拖慢启动并在日志里刷屏，
/// 因此用 <see cref="_availabilityChecked" /> 记住首次探测结果。
/// 探测会在首次被使用时触发（而不是服务启动时），因为此刻 logger 才真正可用。
/// </summary>
public sealed class FfmpegVideoMediaInspector(
    IOptions<VideoMediaOptions> options,
    ILogger<FfmpegVideoMediaInspector> logger) : IVideoMediaInspector
{
    /// <summary>
    /// 探测是否已执行过。
    ///
    /// 刻意做成<b>实例字段</b>而不是 static：可用性取决于 <see cref="Options" />，
    /// 而配置是按实例注入的。若做成 static，第一个探测过的配置会把结论
    /// "缓存"给之后所有实例——于是换了配置（或测试里换配置）之后，
    /// 新配置明明指向真实 ffmpeg，却仍被上一个配置的结论判定为不可用。
    /// </summary>
    private readonly Lock _availabilityLock = new();

    private bool _availabilityChecked;

    private bool _available;

    private VideoMediaOptions Options => options.Value;

    /// <summary>
    /// 当前进程内 ffmpeg 是否可用（首次访问时真正探测一次）。
    ///
    /// 暴露出来有两个用途：
    /// <list type="bullet">
    ///   <item>测试可以直接断言"能跑"与"跑不了"被区分开——
    ///         只看 <see cref="ProbeAsync" /> 的返回值区分不了，
    ///         因为两种情况下它都会因为"视频文件不存在"而返回 null；</item>
    ///   <item>排查问题时可以直接查它，不必去等第一条视频任务失败。</item>
    /// </list>
    /// </summary>
    public bool IsAvailable => EnsureAvailable();

    public async Task<VideoMediaInfo?> ProbeAsync(string filePath, CancellationToken cancellationToken = default)
    {
        if (!EnsureAvailable() || !File.Exists(filePath))
            return null;

        try
        {
            var (exitCode, stdout, _) = await RunAsync(
                ResolveFfprobePath(),
                [
                    "-v", "error",
                    "-select_streams", "v:0",
                    "-show_entries", "stream=width,height",
                    "-show_entries", "format=duration",
                    "-of", "json",
                    filePath,
                ],
                cancellationToken);

            if (exitCode != 0 || string.IsNullOrWhiteSpace(stdout))
                return null;

            using var document = JsonDocument.Parse(stdout);
            var root = document.RootElement;

            // 时长在 format 下，分辨率在第一条视频流下——两个位置都要试，
            // 不同 ffmpeg 版本输出的位置不完全一致。
            var duration = ReadDouble(root, "format", "duration")
                           ?? ReadDouble(root, "streams", 0, "duration");

            int? width = null;
            int? height = null;

            if (root.TryGetProperty("streams", out var streams)
                && streams.ValueKind == JsonValueKind.Array
                && streams.GetArrayLength() > 0)
            {
                var first = streams[0];
                width = ReadInt(first, "width");
                height = ReadInt(first, "height");
            }

            if (duration is null && width is null && height is null)
                return null;

            return new VideoMediaInfo(duration, width, height);
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "探测视频 {FilePath} 的元信息失败，封面与时长将留空", filePath);
            return null;
        }
    }

    public async Task<string?> ExtractThumbnailAsync(
        string videoPath,
        string thumbnailPath,
        CancellationToken cancellationToken = default)
    {
        if (!EnsureAvailable() || !File.Exists(videoPath))
            return null;

        try
        {
            var directory = Path.GetDirectoryName(thumbnailPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            var at = Options.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            var seconds = Options.ThumbnailAtSecond.ToString("0.###", CultureInfo.InvariantCulture);

            // -ss 放在 -i 之前用关键帧快速定位；-frames:v 1 只取一帧即退，
            // -y 覆盖同名文件（重试同一任务时可能已存在）
            var (exitCode, _, stderr) = await RunAsync(
                Options.FfmpegPath,
                [
                    "-hide_banner",
                    "-loglevel", "error",
                    "-ss", seconds,
                    "-i", videoPath,
                    "-frames:v", "1",
                    "-q:v", "3",
                    "-f", "image2",
                    "-y",
                    thumbnailPath,
                ],
                cancellationToken);

            if (exitCode != 0 || !File.Exists(thumbnailPath))
            {
                logger.LogWarning(
                    "抽取视频封面失败（退出码 {ExitCode}）：{Stderr}",
                    exitCode,
                    Truncate(stderr, 300));

                return null;
            }

            return thumbnailPath;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "抽取视频 {VideoPath} 的封面失败", videoPath);
            return null;
        }
    }

    /// <summary>
    /// 探测 ffmpeg 是否真的可用。
    ///
    /// <para>
    /// 这里必须<b>真的跑一次</b>而不能靠猜。曾经的实现是"路径里带分隔符就 File.Exists，
    /// 否则一律当成存在"——于是配置填默认的 <c>ffmpeg</c>、而服务器其实没装时，
    /// 探测会误报"可用"，直到每条视频任务启动进程时才失败，
    /// 结果就是每条任务刷一条异常日志，而最该出现的那条"没装 ffmpeg，请配置路径"反而没了。
    /// </para>
    ///
    /// <para>
    /// 只探测一次并缓存到进程级：ffmpeg 有没有装是<b>环境问题</b>，
    /// 不会在进程生命周期内改变，每条视频都付一次进程启动的代价纯属浪费。
    /// </para>
    /// </summary>
    private bool EnsureAvailable()
    {
        // 快速路径：已探测过就直接返回，避免每条视频都去抢锁
        if (_availabilityChecked)
            return _available;

        lock (_availabilityLock)
        {
            if (_availabilityChecked)
                return _available;

            var available = ProbeAvailability();

            if (available)
            {
                logger.LogInformation(
                    "ffmpeg 可用：{Path}。视频封面与时长信息将正常生成。",
                    Options.FfmpegPath);
            }
            else
            {
                // 封面是锦上添花，视频本身已经生成成功，
                // 因此这里只提示一次、不抛异常，任务照常完成。
                logger.LogWarning(
                    "未找到可用的 ffmpeg（当前配置：{Path}），视频仍会正常生成，只是不带封面、时长与分辨率。" +
                    "如需封面：把 ffmpeg.exe 放到服务器任意目录后，" +
                    "在 GenerateWorker 的 appsettings.json 里配置 VideoMedia:FfmpegPath 为其完整路径；" +
                    "或设置环境变量 VideoMedia__FfmpegPath；也可以直接装进系统 PATH 后留空该项。",
                    Options.FfmpegPath);
            }

            _available = available;
            _availabilityChecked = true;

            return _available;
        }
    }

    /// <summary>
    /// 实际探测：先按文件判断，再真正启动一次 <c>-version</c> 确认能执行。
    /// </summary>
    private bool ProbeAvailability()
    {
        var path = NormalizePath(Options.FfmpegPath);

        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            // 用户明确给了路径（而不是裸命令名）时，文件不存在要立刻拒绝并说清楚，
            // 否则用户会以为是"装上了但没生效"
            var looksLikePath = path.Contains(Path.DirectorySeparatorChar)
                                || path.Contains(Path.AltDirectorySeparatorChar)
                                || Path.IsPathRooted(path);

            if (looksLikePath && !File.Exists(path))
            {
                logger.LogWarning("配置的 ffmpeg 路径不存在：{Path}", path);
                return false;
            }

            // 跑一次 -version：这是唯一能同时证明"文件在"且"能执行"的检查。
            // 裸命令名在 PATH 里找不到时 Process.Start 会抛 Win32Exception，正好用来判别。
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.StartInfo.ArgumentList.Add("-version");

            process.Start();

            // 必须把两个流都读干，否则子进程写满管道缓冲区后会阻塞等待读取方，
            // 永远不退出——表现就是"ffmpeg 明明装了却判定为不可用"。
            // ffmpeg -version 的输出有几 KB，正好可能撞上缓冲区上限。
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();

            // 只等一小会儿：探测不应该拖慢启动，
            // 即使碰上卡住的脚本也不至于让第一条视频等半天
            if (!process.WaitForExit(5000))
            {
                TryKill(process);
                return false;
            }

            // WaitForExit()（无参）会等待异步流读完，这里必须再调一次，
            // 否则可能拿到不完整的 stdout
            process.WaitForExit();

            return process.ExitCode == 0;
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "探测 ffmpeg {Path} 可用性失败", path);
            return false;
        }
    }

    /// <summary>
    /// 归一化用户填的路径。
    ///
    /// 从聊天记录 / 网页复制粘贴带出来的路径经常带着首尾引号或多余空格
    /// （例如从 PowerShell 复制的 <c>"D:\tools\ffmpeg.exe"</c>），
    /// 这些字符会被当成文件名的一部分，直接导致"明明配了却说找不到"。
    /// 这里统一去掉，顺带把 Windows 的反斜杠也交给 File APIs 自己处理。
    /// </summary>
    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        return path.Trim().Trim('"').Trim();
    }

    /// <summary>
    /// 决定 ffprobe 的路径。
    ///
    /// <para>
    /// 用户常常只从官网下载 <c>ffmpeg.exe</c> 一个文件（静态包里两者其实是分开的两个 exe），
    /// 此时"沿用 ffmpeg 同目录"会推出一个并不存在的 ffprobe 路径。
    /// 因此这里<b>先看显式配置，再看同目录是否存在，最后才回退到 PATH</b>——
    /// 回退是有意义的：机器上装了 ffmpeg 套件时，ffprobe 通常也在 PATH 里。
    /// </para>
    /// </summary>
    private string ResolveFfprobePath()
    {
        // 1) 显式配置优先，用户最清楚自己的环境
        var configured = NormalizePath(Options.FfprobePath);

        if (configured.Length > 0)
            return configured;

        // 2) ffmpeg 配了完整路径时，尝试同目录的 ffprobe
        var ffmpeg = NormalizePath(Options.FfmpegPath);
        var directory = Path.GetDirectoryName(ffmpeg);

        if (!string.IsNullOrEmpty(directory) && Path.IsPathRooted(ffmpeg))
        {
            var sibling = Path.Combine(directory, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

            if (File.Exists(sibling))
                return sibling;
        }

        // 3) 回退到 PATH
        return "ffprobe";
    }

    private async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        // <b>必须先 Start 再读流</b>：Process 在启动之前访问 StandardOutput
        // 会抛 "StandardOut has not been redirected or the process hasn't started yet."。
        //
        // 这个顺序写反过一次，而症状极具迷惑性：
        // ffmpeg 明明装好了、配置也正确，探测却返回 null，
        // 封面与时长全部为空——因为异常被上层的 catch 吞掉、只留下一条 Warning，
        // 看起来像"ffmpeg 不可用"，排查方向完全被带偏。
        // （真实 ffmpeg 的集成测试就是为了钉住这类问题才加的）
        process.Start();

        // 启动后立刻开始读干两个流：子进程写满管道缓冲区后会阻塞等读取方，
        // 不并发读就可能永远等不到它退出
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, Options.TimeoutSeconds)));

        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 超时不是业务失败：杀掉进程并如实返回，让调用方走「留空」分支
            TryKill(process);
            throw new TimeoutException($"执行 {fileName} 超时（超过 {Options.TimeoutSeconds} 秒）。");
        }

        return (process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已自行退出，忽略
        }
    }

    private static double? ReadDouble(JsonElement root, params object[] path)
    {
        var element = Navigate(root, path);

        if (element is null)
            return null;

        // Navigate 返回的是可空 JsonElement，这里取出值本体再判断类型
        var value = element.Value;

        // 必须同时处理数字与字符串两种形态。
        // ffprobe 的 format.duration 输出的是 <b>字符串</b>（"3.000000"），
        // 而 width/height 是数字——同一个 JSON 里两种都有。
        // 只认数字会导致时长永远解析成 null，
        // 表现为"分辨率有、时长没有"，很容易被误当成正常现象而没人去查。
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number))
            return number;

        if (value.ValueKind == JsonValueKind.String
            && double.TryParse(
                value.GetString(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var text))
        {
            return text;
        }

        return null;
    }

    private static int? ReadInt(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed)
            ? parsed
            : null;
    }

    private static JsonElement? Navigate(JsonElement root, object[] path)
    {
        JsonElement current = root;

        foreach (var segment in path)
        {
            if (segment is int index)
            {
                if (current.ValueKind != JsonValueKind.Array || index >= current.GetArrayLength())
                    return null;

                current = current[index];
                continue;
            }

            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(segment.ToString()!, out var next))
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max];
}