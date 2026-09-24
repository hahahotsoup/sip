using System.Net;
using Xunit;

namespace Sip.Tests;

/// <summary>
/// 阅读报告接口的契约：**没有数据就说没有数据**。
///
/// 背景：这一页以前渲染的是一组**编造的统计数字**（写死的"阅读 23 / 收藏 6 / 抓全文 4"
/// 之类），看起来像你的真实阅读情况。现在接口只报事实，这个用例把"不许编"钉在接口层：
/// 遥测默认关闭 → 必须回 TELEMETRY_OFF，**绝不能**给出一份看起来正常的报告。
///
/// 复用 <see cref="WebSanitizeFixture"/>（它其实就是通用的"真起 sip --start + 带 cookie 访问"
/// 夹具，名字是历史遗留）。夹具实例的遥测一律是默认关闭状态 —— 正好是要测的这一支。
/// </summary>
public class WebInsightsTests : IClassFixture<WebSanitizeFixture>
{
    private readonly WebSanitizeFixture _fx;

    public WebInsightsTests(WebSanitizeFixture fx) => _fx = fx;

    [Fact]
    public async Task TelemetryOff_ReturnsTelemetryOff_NotAFabricatedReport()
    {
        using var client = _fx.NewClient(out var jar);
        await _fx.ConsumeBootTokenAsync(client, jar);

        using var res = await client.GetAsync("/api/insights");

        Assert.Equal(HttpStatusCode.Conflict, res.StatusCode);
        string body = await res.Content.ReadAsStringAsync();
        Assert.Contains("TELEMETRY_OFF", body);
        Assert.Contains("telemetry enable", body);      // 要给出可执行的下一步，而不是只报错

        // 关键：不许夹带任何"看起来像报告"的字段。
        // 这几条是这个用例存在的全部理由 —— 页面上再出现编造数字，就从这里红。
        Assert.DoesNotContain("\"feeds\"", body);
        Assert.DoesNotContain("\"opened\"", body);
        Assert.DoesNotContain("\"completionRate\"", body);
        Assert.DoesNotContain("\"windowDays\"", body);
    }
}
