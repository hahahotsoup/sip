// 把引导链接换钥匙的**响应头**原样打出来 —— 判断 cookie 是没种上、名字不对、还是被拒。
import { readFileSync } from "node:fs";

/* 从终端横幅里解析引导令牌。
   ⚠ 不要用「PowerShell 先把令牌写进小文件、node 再读」那条路：
   Set-Content 会带 UTF-8 BOM，读出来是 "<BOM>0967d…"，拿去换票必然失败，
   而失败的样子（服务端回引导页 + 吊销 cookie）看起来像**服务端的 bug**。
   直接从横幅解析就没有中间编码这一步。 */
function bootToken() {
  const banner = readFileSync(process.env.SIP_BANNER_FILE, "utf8");
  const m = banner.match(/\?t=([0-9a-fA-F]{16,})/);
  if (!m) {
    console.error("横幅里找不到 ?t= 引导令牌：\n" + banner);
    process.exit(2);
  }
  return m[1];
}

const BASE = process.env.SIP_BASE || "http://127.0.0.1:8778";
const TOKEN = bootToken();

const show = (label, r) => {
  console.log(`\n=== ${label} ===`);
  console.log(`  HTTP ${r.status}`);
  for (const [k, v] of r.headers.entries()) console.log(`  ${k}: ${v}`);
  const sc = r.headers.getSetCookie?.() || [];
  console.log(`  getSetCookie() -> ${sc.length} 条`);
  for (const c of sc) console.log(`     ${c}`);
};

let r = await fetch(`${BASE}/?t=${TOKEN}`, { redirect: "manual" });
show("GET /?t=<token>", r);
const raw = r.headers.getSetCookie?.() || [];
const cookieHeader = raw.map(c => c.split(";")[0]).join("; ");
console.log(`\n  组装出的 Cookie 头: ${cookieHeader || "(空)"}`);

r = await fetch(`${BASE}/`, { headers: { Cookie: cookieHeader, "User-Agent": "Mozilla/5.0" }, redirect: "manual" });
show("GET / 带 Cookie", r);
const body = await r.text();
console.log(`  body len=${body.length}  title=${(body.match(/<title>(.*?)<\/title>/) || [])[1] || "?"}`);
console.log(`  含 aiOrb: ${body.includes('id="aiOrb"')}`);

// 直接观察 cookie 的路径/域有没有把根路径排除掉
console.log("\n=== 换成复刻浏览器行为：先跟随重定向，再共用 cookie jar ===");
r = await fetch(`${BASE}/?t=${TOKEN}`, { redirect: "follow" });
const jar = (r.headers.getSetCookie?.() || []).map(c => c.split(";")[0]).join("; ");
const loc = r.url;
console.log(`  最终 url=${loc}  HTTP ${r.status}  jar=${jar || "(无)"}`);
const b2 = await r.text();
console.log(`  title=${(b2.match(/<title>(.*?)<\/title>/) || [])[1] || "?"}  len=${b2.length}  含 aiOrb: ${b2.includes('id="aiOrb"')}`);
