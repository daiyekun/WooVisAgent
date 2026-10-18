using System.Text.Json;
using System.Text.Json.Serialization;

namespace WooVisAgent.SharedLib.Domain;

/// <summary>
/// 视频生成的输入模式。对齐即梦（Seedance）的三种「参考模式」：
/// <list type="bullet">
///   <item><b>全能生成</b>：不传素材就是纯文生视频；传素材则用图 / 视频 / 音频多模态参考，
///   在提示词里用 <c>@图片1</c>、<c>@视频1</c>、<c>@音频1</c> 指定各素材的角色。</item>
///   <item><b>首尾帧</b>：恰好两张图，中间过程由提示词描述。</item>
///   <item><b>智能多帧</b>：多张关键帧串联成一镜到底。</item>
/// </list>
///
/// 三者是<b>互斥</b>的：火山方舟明确要求首帧 / 首尾帧 / 全模态参考不能混在一次请求里，
/// 因此这里不做"多选"，只允许单选。
/// </summary>
[JsonConverter(typeof(VideoGenerationModeJsonConverter))]
public enum VideoGenerationMode
{
    /// <summary>全能生成（默认）。无素材=文生视频，有素材=多模态参考</summary>
    Omni = 1,

    /// <summary>首尾帧。首图→尾图，中间过渡由提示词产生</summary>
    FirstLastFrame = 2,

    /// <summary>智能多帧。多张关键帧按顺序串联</summary>
    Keyframes = 3,
}

public static class VideoGenerationModeExtensions
{
    /// <summary>转成前端使用的小写标识</summary>
    public static string ToWireValue(this VideoGenerationMode mode) => mode switch
    {
        VideoGenerationMode.FirstLastFrame => "firstLastFrame",
        VideoGenerationMode.Keyframes => "keyframes",
        _ => "omni",
    };

    /// <summary>解析前端传入的标识；未知值一律退回全能生成，不报错</summary>
    public static VideoGenerationMode ParseWireValue(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "firstlastframe" or "首尾帧" => VideoGenerationMode.FirstLastFrame,
        "keyframes" or "multiFrame" or "智能多帧" => VideoGenerationMode.Keyframes,
        _ => VideoGenerationMode.Omni,
    };

    /// <summary>中文展示名，直接用于界面下拉</summary>
    public static string ToDisplayName(this VideoGenerationMode mode) => mode switch
    {
        VideoGenerationMode.FirstLastFrame => "首尾帧",
        VideoGenerationMode.Keyframes => "智能多帧",
        _ => "全能生成",
    };

    /// <summary>下拉里的一句话说明，让用户不看文档也知道该传什么</summary>
    public static string ToHint(this VideoGenerationMode mode) => mode switch
    {
        VideoGenerationMode.FirstLastFrame => "上传两张图：第一张是开头，最后一张是结尾，中间的过程由提示词描述。",
        VideoGenerationMode.Keyframes => "上传多张图作为关键帧，模型自动插值成一镜到底的视频。",
        _ => "只输入文字就是文生视频；也可以上传图片 / 视频 / 音频，在提示词里用 @图片1、@视频1、@音频1 指定它们的用途。",
    };
}

/// <summary>
/// 让 <see cref="VideoGenerationMode" /> 以 <c>omni</c> / <c>firstLastFrame</c> / <c>keyframes</c>
/// 的形式出入 JSON，而不是默认的数字。
///
/// <para>不加这个转换器，接口会把能力声明里的模式吐成 <c>[1,2,3]</c>，
/// 前端拿到三个数字根本没法和下拉选项对上——而且这个缺陷只在真正调用时才暴露：
/// 单测断言的是 C# 枚举，看不出序列化结果。</para>
///
/// <para>读取时同时接受数字，兼容早期落库的 DeliveryJson。</para>
/// </summary>
public sealed class VideoGenerationModeJsonConverter : JsonConverter<VideoGenerationMode>
{
    public override VideoGenerationMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetInt32(out var number) && Enum.IsDefined(typeof(VideoGenerationMode), number)
                => (VideoGenerationMode)number,
            JsonTokenType.String => VideoGenerationModeExtensions.ParseWireValue(reader.GetString()),
            _ => VideoGenerationMode.Omni,
        };
    }

    public override void Write(Utf8JsonWriter writer, VideoGenerationMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToWireValue());
}

/// <summary>参考素材的类型</summary>
public enum VideoReferenceKind
{
    Image = 1,
    Video = 2,

    /// <summary>音频参考（参考音色 / 背景音）。Seedance 2.0 及以上支持</summary>
    Audio = 3,
}

public static class VideoReferenceKindExtensions
{
    public static string ToWireValue(this VideoReferenceKind kind) => kind switch
    {
        VideoReferenceKind.Video => "video",
        VideoReferenceKind.Audio => "audio",
        _ => "image",
    };

    public static bool TryParseWireValue(string? value, out VideoReferenceKind kind)
    {
        kind = VideoReferenceKind.Image;

        switch (value?.Trim().ToLowerInvariant())
        {
            case "image":
                kind = VideoReferenceKind.Image;
                return true;
            case "video":
                kind = VideoReferenceKind.Video;
                return true;
            case "audio":
                kind = VideoReferenceKind.Audio;
                return true;
            default:
                return false;
        }
    }

    /// <summary>@ 引用时的编号前缀，与即梦保持一致（@图片1 / @视频1 / @音频1）</summary>
    public static string ToReferenceLabel(this VideoReferenceKind kind) => kind switch
    {
        VideoReferenceKind.Video => "视频",
        VideoReferenceKind.Audio => "音频",
        _ => "图片",
    };
}