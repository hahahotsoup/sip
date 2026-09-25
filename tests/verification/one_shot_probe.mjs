// 单次干净序列：换票 → 立刻用同一把钥匙请求 /。
// 期间**不做任何别的请求**（之前的探针反复用同一个一次性令牌换票，
// 把"令牌已被消费"和"钥匙无效"两件事混在了一起，越查越乱）。
import { readFileSync } from "node:fs";

const BASE = process.env.SIP_BASE || "http://127.0.0.1:8778";
const banner = readFileSync(process.env.SIP_BANNER_FILE, "utf8");
const TOKEN = (banner.match(/\?t=([0-9a-fA-F]{16,})/) || [])[1];
console.log(`令牌 = ${TOKEN}`);

/* ⚠ 换票那一步**必须带上和后续请求一样的 User-Agent**。
   sip 把会话钥匙绑在浏览器指纹（UA 的 SHA256）上：IssueWebSession 记下当时请求的指纹，
   LookupWebSession 逐字节比对，不一致就**当场吊销**（Web.cs:2377/2399-2412）。
   这个保护本身是对的（抄走 cookie 换浏览器也进不来），但它意味着
   「换票不带 UA、后续带 UA」会被判成两个浏览器 —— 看起来完全像服务端的 bug。
   UA 是这个探针里唯一的身份变量，必须全程一致。 */
const UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) sip-harness-probe";
const H = () => ({ "User-Agent": UA });

// 1) 换票（带 UA）
const r1 = await fetch(`${BASE}/?t=${TOKEN}`, { redirect: "manual", headers: H() });
const setCookie = (r1.headers.getSetCookie?.() || []);
console.log(`\n1) GET /?t=… -> HTTP ${r1.status}  location=${r1.headers.get("location")}`);
console.log(`   set-cookie: ${setCookie.join(" | ") || "(无)"}`);
if (!setCookie.length) { console.log("换票没给 cookie —— 令牌没被接受"); process.exit(1); }

const nameVal = setCookie[0].split(";")[0];
const name = nameVal.split("=")[0];
const value = nameVal.split("=")[1];
console.log(`   解析: name=${name}  value=${value?.slice(0, 16)}… (${value?.length} 字符)`);

// 2) 立刻带这把钥匙请求 /
const r2 = await fetch(`${BASE}/`, { headers: { Cookie: nameVal, ...H() }, redirect: "manual" });
const body = await r2.text();
console.log(`\n2) GET / 带 Cookie -> HTTP ${r2.status}  len=${body.length}`);
console.log(`   title=${(body.match(/<title>(.*?)<\/title>/) || [])[1] || "?"}`);
console.log(`   含 aiOrb=${body.includes('id="aiOrb"')}  含 aiDel=${body.includes('id="aiDel"')}`);
console.log(`   服务端是否回收 cookie: ${(r2.headers.getSetCookie?.() || []).join(" | ") || "(没有回收)"}`);

// 3) 同一把钥匙请求 API
const r3 = await fetch(`${BASE}/api/imports`, { headers: { Cookie: nameVal, ...H() } });
console.log(`\n3) GET /api/imports 带同一把钥匙 -> HTTP ${r3.status}`);
console.log(`   ${(await r3.text()).slice(0, 200)}`);

process.exit(body.includes('id="aiOrb"') ? 0 : 1);
