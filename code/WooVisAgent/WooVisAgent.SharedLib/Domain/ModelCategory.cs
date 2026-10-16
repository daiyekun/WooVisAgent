namespace WooVisAgent.SharedLib.Domain;

/// <summary>
/// 模型分类。与前端 src/types/index.ts 的 ModelCategory 一一对应，
/// 对外传输时统一转成小写字符串（image / video / llm）。
/// </summary>
public enum ModelCategory
{
    /// <summary>图片模型（文生图 / 图生图）</summary>
    Image = 1,

    /// <summary>视频模型（文生视频 / 图生视频）</summary>
    Video = 2,

    /// <summary>语言模型（提示词润色、多轮对话等）</summary>
    Llm = 3,
}

public static class ModelCategoryExtensions
{
    /// <summary>转成前端使用的小写分类标识</summary>
    public static string ToWireValue(this ModelCategory category) => category switch
    {
        ModelCategory.Image => "image",
        ModelCategory.Video => "video",
        ModelCategory.Llm => "llm",
        _ => "image",
    };

    /// <summary>解析前端传入的分类标识，忽略大小写</summary>
    public static bool TryParseWireValue(string? value, out ModelCategory category)
    {
        category = ModelCategory.Image;

        if (string.IsNullOrWhiteSpace(value))
            return false;

        switch (value.Trim().ToLowerInvariant())
        {
            case "image":
                category = ModelCategory.Image;
                return true;
            case "video":
                category = ModelCategory.Video;
                return true;
            case "llm":
                category = ModelCategory.Llm;
                return true;
            default:
                return false;
        }
    }
}
