// 确认「浏览器真正拿到的那份 index.html」里有悬浮球和删除按钮。
//
// 为什么值得单独查：/ 在**未认证**时返回的是引导页（也是 HTML，也有 <title>），
// 拿错那一份就会得出"按钮不在"的错误结论 —— 这个坑踩过一次。
// 另外两个坑（都踩过）：令牌别经 PowerShell 文件转手（BOM），UA 必须全程一致（会话指纹）。
import { readFileSync } from "node:fs";

const BASE = process.env.SIP_BASE || "http://127.0.0.1:8778";
const UA = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) sip-harness-probe";
const H = (extra = {}) => ({ "User-Agent": UA, ...extra });

const TOKEN = (readFileSync(process.env.SIP_BANNER_FILE, "utf8").match(/\?t=([0-9a-fA-F]{16,})/) || [])[1];
if (!TOKEN) { console.error("横幅里找不到 ?t= 引导令牌"); process.exit(2); }

let cookies = [];
const jar = () => cookies.map(c => c.split(";")[0]).join("; ");
const grab = (r) => { for (const c of (r.headers.getSetCookie?.() || [])) cookies.push(c.split(";")[0]); };

const boot = await fetch(`${BASE}/?t=${TOKEN}`, { redirect: "manual", headers: H() });
grab(boot);
console.log(`引导链接 -> HTTP ${boot.status}  cookies=${cookies.join(", ") || "(无)"}`);
if (!cookies.length) { console.error("换票没拿到 cookie"); process.exit(2); }

const r = await fetch(`${BASE}/`, { headers: H({ Cookie: jar() }) });
const h = await r.text();
const title = (h.match(/<title>(.*?)<\/title>/) || [])[1] || "(无 title)";
console.log(`认证后 / -> HTTP ${r.status}  len=${h.length}  title=${title}`);
if (title.includes("引导链接") || title.includes("登录")) {
  console.error("拿到的还是引导页/登录页 —— 这份不是真页面，不能据此判断按钮在不在");
  process.exit(2);
}

const wanted = [
  'data-act="ai-del"', 'id="aiDel"', 'data-act="ai-new"', 'data-act="ai-close"',
  'id="aiOrb"', 'id="aiPanel"', 'id="aiMsgs"', 'id="aiQ"', 'id="aiSel"', 'id="aiTip"',
];
let miss = 0;
for (const k of wanted) {
  const ok = h.includes(k);
  if (!ok) miss++;
  console.log(`  ${ok ? "OK  " : "MISS"}  ${k}`);
}

console.log("\n头部图标按钮（顺序即界面顺序）:");
for (const b of (h.match(/<button class="ib"[^>]*>[^<]*<\/button>/g) || [])) console.log("   " + b.trim());

const a = await fetch(`${BASE}/app.js`, { headers: H({ Cookie: jar() }) });
const js = await a.text();
console.log(`\n/app.js -> HTTP ${a.status}  len=${js.length}`);
for (const k of ["aiDelete", "?yes=1", "all chats for this book", "aiHandleFrame"]) {
  const ok = js.includes(k);
  if (!ok) miss++;
  console.log(`  ${ok ? "OK  " : "MISS"}  ${k}`);
}

console.log(miss ? `\n结果: ${miss} 处缺失` : "\n结果: 全部就位");
process.exit(miss ? 1 : 0);
