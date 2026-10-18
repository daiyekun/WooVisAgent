using System.Text.Json;
using WooVisAgentServices.AgentService.Support;

namespace WooVisAgent.Api.Tests;

/// <summary>
/// 模型返回 JSON 的容错解析。
///
/// 这几条用例全部来自实测：qwen-plus 在「要求只输出 JSON」时，
/// 依然会加代码块、加一句客套话，甚至把整段结构再包成字符串
/// （<c>{"prompt":"{\"prompt\":\"...\"}"}</c>）。
/// 解析不了的结果不只是难看——视频规划会退化成「模型未按结构返回」，参数全空。
/// </summary>
public class AgentJsonTests
{
    [Fact]
    public void 干净的JSON直接解析()
    {
        using var document = AgentJson.TryParseObject("""{"prompt":"一只猫","resolution":"720P"}""");

        Assert.NotNull(document);
        Assert.Equal("一只猫", document!.RootElement.ReadString("prompt"));
        Assert.Equal("720P", document.RootElement.ReadString("resolution"));
    }

    [Fact]
    public void 带Markdown代码块也能解析()
    {
        var raw = """
            ```json
            {"prompt":"海边日落","ratio":"16:9"}
            ```
            """;

        using var document = AgentJson.TryParseObject(raw);
        Assert.NotNull(document);
        Assert.Equal("海边日落", document!.RootElement.ReadString("prompt"));
    }

    [Fact]
    public void 前后有客套话也能解析()
    {
        using var document = AgentJson.TryParseObject("好的，以下是结果：{\"prompt\":\"雨夜街头\"} 希望有帮助");

        Assert.NotNull(document);
        Assert.Equal("雨夜街头", document!.RootElement.ReadString("prompt"));
    }

    [Fact]
    public void 整段JSON被包成字符串时能解开()
    {
        var raw = "\"{\\\"prompt\\\":\\\"雨夜霓虹\\\",\\\"durationSeconds\\\":5}\"";

        using var document = AgentJson.TryParseObject(raw);
        Assert.NotNull(document);
        Assert.Equal("雨夜霓虹", document!.RootElement.ReadString("prompt"));
        Assert.Equal(5, document.RootElement.ReadInt("durationSeconds"));
    }

    [Fact]
    public void 字段里又装了一份JSON时能拆到内层()
    {
        // 实测 qwen-plus 会这样返回：整段答案被塞进 polished 字段
        var raw = """
            {"polished":"{\"polished\":\"一只猫慵懒地趴在窗台上\",\"negative\":\"模糊\",\"tips\":[\"强调光斑\"]}"}
            """;

        var keepAlive = new List<JsonDocument>();
        try
        {
            var document = AgentJson.TryParseObject(raw);
            Assert.NotNull(document);
            keepAlive.Add(document!);

            var root = document!.RootElement.UnwrapStringified(
                "polished", keepAlive, element => element.ReadString("polished") is not null);

            Assert.Equal("一只猫慵懒地趴在窗台上", root.ReadString("polished"));
            Assert.Equal("模糊", root.ReadString("negative"));
            Assert.Single(root.ReadStringArray("tips"));
        }
        finally
        {
            foreach (var document in keepAlive)
                document.Dispose();
        }
    }

    [Fact]
    public void 普通提示词不会被误当成嵌套结构拆掉()
    {
        // prompt 里是正常的画面描述（含大括号，但不是 JSON），必须原样保留
        const string raw = "{\"prompt\":\"一间摆着 {\\\"书架\\\"} 的书房\",\"resolution\":\"720P\"}";

        var keepAlive = new List<JsonDocument>();
        try
        {
            var document = AgentJson.TryParseObject(raw);
            keepAlive.Add(document!);

            var root = document!.RootElement.UnwrapStringified(
                "prompt", keepAlive, element => element.ReadString("modelId") is not null);

            Assert.Equal("一间摆着 {\"书架\"} 的书房", root.ReadString("prompt"));
            Assert.Equal("720P", root.ReadString("resolution"));
        }
        finally
        {
            foreach (var document in keepAlive)
                document.Dispose();
        }
    }

    [Fact]
    public void 数组值转成字符串列表()
    {
        using var document = AgentJson.TryParseObject("""{"notes":["强调雨丝","镜头上摇"]}""");

        Assert.NotNull(document);
        Assert.Equal(2, document!.RootElement.ReadStringArray("notes").Count);
    }

    [Fact]
    public void 完全不是JSON时返回空由调用方降级()
    {
        Assert.Null(AgentJson.TryParseObject("抱歉，我无法完成这个请求。"));
        Assert.Null(AgentJson.TryParseObject(""));
        Assert.Null(AgentJson.TryParseObject(null));
    }

    // ---- 不依赖整段 JSON 合法的按字段提取 --------------------------------

    [Fact]
    public void 整段JSON非法时仍能按字段取到值()
    {
        // 线上真实形态：长中文里混入未转义内容，JsonDocument.Parse 直接失败，
        // 但字段本身是完整的，按 key 定位照样能取出来
        const string raw = "{\"polished\":\"一只猫在窗台上晒太阳\",\"negative\":\"模糊、水印\",\"tips\":[\"强调光斑\",\"虚化背景\"]}";

        Assert.Equal("一只猫在窗台上晒太阳", AgentJson.ExtractString(raw, "polished"));
        Assert.Equal("模糊、水印", AgentJson.ExtractString(raw, "negative"));
        Assert.Equal(2, AgentJson.ExtractStringArray(raw, "tips").Count);
    }

    [Fact]
    public void 未转义的引号导致JSON非法时仍能取出提示词()
    {
        const string raw = "{\"polished\":\"一只猫\"说\"了一声\",\"negative\":\"模糊\"}";

        Assert.Null(AgentJson.TryParseObject(raw));
        Assert.Equal("一只猫", AgentJson.ExtractString(raw, "polished"));
        Assert.Equal("模糊", AgentJson.ExtractString(raw, "negative"));
    }

    [Fact]
    public void 取数字字段兼容字符串与裸数字()
    {
        Assert.Equal(5, AgentJson.ExtractInt("{\"durationSeconds\":5}", "durationSeconds"));
        Assert.Equal(8, AgentJson.ExtractInt("{\"durationSeconds\":\"8\"}", "durationSeconds"));
        Assert.Null(AgentJson.ExtractInt("{\"durationSeconds\":\"很久\"}", "durationSeconds"));
    }

    [Theory]
    // 整段被 JSON 字符串包裹
    [InlineData("\"{\\\"polished\\\":\\\"一只猫\\\"}\"")]
    // 字段里又装了一份
    [InlineData("{\"polished\":\"{\\\"polished\\\":\\\"一只猫\\\"}\"}")]
    // 外层还有客套话
    [InlineData("好的：{\"polished\":\"一只猫\"} 以上")]
    public void 三种包裹方式都能拿到同一段提示词(string raw)
    {
        var text = AgentJson.Unwrap(raw, "polished");
        Assert.Equal("一只猫", AgentJson.ExtractString(text, "polished"));
    }

    [Fact]
    public void 模型只返回纯文本时保留原文()
    {
        const string raw = "抱歉，我没能生成结构化结果";

        var text = AgentJson.Unwrap(raw, "polished");
        Assert.Null(AgentJson.ExtractString(text, "polished"));
        Assert.Equal(raw, text.Trim());
    }
}
