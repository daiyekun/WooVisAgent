using WooVisAgentServices.ModelService.Dtos;

namespace WooVisAgentServices.ModelService.Providers;

/// <summary>按供应方拉取可用模型清单，供表单下拉预填</summary>
public interface IModelProviderCatalog
{
    IReadOnlyList<ProviderModelDto> GetModels(string provider);
}

/// <summary>
/// 内置的常见供应方模型清单。用户可自行填写 modelKey，这里只做下拉提示，
/// 后续接入各供应方的 /models 接口时可替换为在线查询实现。
/// </summary>
public sealed class StaticModelProviderCatalog : IModelProviderCatalog
{
    private static readonly Dictionary<string, IReadOnlyList<ProviderModelDto>> Catalog =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI"] =
            [
                new("gpt-image-1", "GPT Image 1"),
                new("dall-e-3", "DALL·E 3"),
                new("sora-1", "Sora 1"),
                new("gpt-4o-mini", "GPT-4o mini"),
            ],
            ["AliyunBailian"] =
            [
                new("wanx2.1-t2i-turbo", "通义万相 文生图 Turbo"),
                new("wanx2.1-i2v-turbo", "通义万相 图生视频 Turbo"),
                new("qwen-max", "通义千问 Max"),
            ],
            ["VolcengineJimeng"] =
            [
                new("jimeng-3.0-t2i", "即梦 3.0 文生图"),
                new("jimeng-3.0-i2v", "即梦 3.0 图生视频"),
                new("doubao-pro-32k", "豆包 Pro 32K"),
            ],
            ["Kling"] =
            [
                new("kling-v1-5", "可灵 1.5"),
                new("kling-v1", "可灵 1.0"),
            ],
            // 本地部署由用户自行填写模型名，ComfyUI 通常使用工作流 ID
            ["ComfyUI"] = [],
            ["Custom"] = [],
        };

    public IReadOnlyList<ProviderModelDto> GetModels(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
            return [];

        return Catalog.TryGetValue(provider.Trim(), out var models) ? models : [];
    }
}
