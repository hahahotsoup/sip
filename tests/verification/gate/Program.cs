// 验证侧工具：开关 sip 的 Agent 门（只动 `agent_ok_<key>` 一条凭据）。
// 用法: gate open <keyName> | gate close <keyName>
// 信用自 tests/Sip.Tests/TestHost.cs 的 OpenAgentGate（同一套 ktsu API、同一服务名 hotsoupreader）。
using ktsu.CredentialCache;
using ktsu.CredentialCache.Storage;

if (args.Length < 2)
{
    Console.WriteLine("usage: gate open|close <keyName>");
    return 3;
}

string mode = args[0];
string key = args[1];
string gateKey = "agent_ok_" + key;

var store = CredentialStoreFactory.CreateDefault("hotsoupreader");
var cache = new ktsu.CredentialCache.CredentialCache(store);
var persona = new PersonaGUID { WeakString = gateKey };

if (mode == "open")
{
    cache.AddOrReplace(persona,
        new CredentialWithToken { Token = new CredentialToken { WeakString = "on" } });
    // 写完回读确认：凭据库写失败时若不核对，症状会变成一堆莫名其妙的 AGENT_BLOCKED
    bool ok = cache.TryGet(persona, out var back)
              && back is CredentialWithToken ct && ct.Token.WeakString == "on";
    Console.WriteLine((ok ? "GATE_OPEN ok " : "GATE_OPEN FAILED ") + gateKey);
    return ok ? 0 : 1;
}

if (mode == "close")
{
    // 先按 ktsu API 删；删不掉就落回 cmdkey（存在性判定不依赖同一套机制）
    bool removed = false;
    try
    {
        var m = cache.GetType().GetMethod("Remove", new[] { typeof(PersonaGUID) });
        if (m is not null) { m.Invoke(cache, new object[] { persona }); removed = true; }
    }
    catch { /* 下面回读兜底 */ }

    bool gone = !cache.TryGet(persona, out _);
    Console.WriteLine((gone ? "GATE_CLOSED ok " : "GATE_CLOSED FAILED ") + gateKey + (removed ? "" : " (no Remove API)"));
    return gone ? 0 : 1;
}

Console.WriteLine("unknown mode: " + mode);
return 3;
