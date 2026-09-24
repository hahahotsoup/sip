using System.Text.Json;
using Xunit;

namespace Sip.Tests;

/// <summary>
/// 译文完整性：**zh-CN 里有的键，zh-Moe 必须也有**。
///
/// 为什么需要这条：`Lang.T("...")` 的键就是英文原文，查不到就**回落显示英文**。
/// 于是漏译不会报错，只会让用户看到一堆英文键（2026-09-12 实测：zh-Moe 比 zh-CN
/// 少 41 条，全是 `ingest` 那批，喵化界面里突然冒英文）。
/// 这条用例把「漏译」变成红色，而不是靠人肉发现。
///
/// 注意它**只保证一个方向**（zh-CN ⊆ zh-Moe）。反方向不保证：译文文件里可能存在
/// 已废弃的旧键，或某个键只在一个文件里被补过——那属于清理问题，不是"显示英文"问题。
/// 也不检查 en-US：它的定位是"英文原文的显式覆盖表"，本来就不要求键齐全。
/// </summary>
public class LangParityTests
{
    private static string RepoRoot()
    {
        // 从测试输出目录向上找 languages/zh-CN.json（找不到就明确失败，不要静默跳过）
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string probe = Path.Combine(dir.FullName, "languages", "zh-CN.json");
            if (File.Exists(probe)) return dir.FullName;
        }
        throw new InvalidOperationException(
            $"找不到仓库根的 languages/zh-CN.json（从 {AppContext.BaseDirectory} 向上找了 8 层）");
    }

    private static HashSet<string> KeysOf(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in doc.RootElement.EnumerateObject()) set.Add(p.Name);
        return set;
    }

    [Fact]
    public void ZhMoe_CoversEveryKeyThatZhCnHas()
    {
        string root = RepoRoot();
        var cn = KeysOf(Path.Combine(root, "languages", "zh-CN.json"));
        var moe = KeysOf(Path.Combine(root, "languages", "zh-Moe.json"));

        Assert.True(cn.Count > 700, $"zh-CN 键数异常（{cn.Count}），是不是读错文件了？");

        var missing = cn.Where(k => !moe.Contains(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(missing.Count == 0,
            $"zh-Moe 缺 {missing.Count} 条译文，这些键会回落显示英文：\n  " +
            string.Join("\n  ", missing.Take(30)) +
            (missing.Count > 30 ? $"\n  …… 还有 {missing.Count - 30} 条" : ""));
    }
}
