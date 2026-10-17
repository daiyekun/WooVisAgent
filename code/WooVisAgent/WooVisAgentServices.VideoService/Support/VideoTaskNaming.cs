namespace WooVisAgentServices.VideoService.Support;

/// <summary>任务名称生成规则。规则与图片侧一致，未填名称时取提示词首行截断。</summary>
public static class VideoTaskNaming
{
    private const int MaxNameLength = 60;

    /// <summary>用户未填写任务名称时，取提示词首行截断作为名称</summary>
    public static string FromPrompt(string? prompt, string fallback = "视频生成任务")
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return fallback;

        var firstLine = prompt
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);

        if (string.IsNullOrWhiteSpace(firstLine))
            return fallback;

        return firstLine.Length <= MaxNameLength ? firstLine : $"{firstLine[..MaxNameLength]}…";
    }
}