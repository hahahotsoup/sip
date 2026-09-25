// 语言文件闸门：三个文件都得是有效 JSON，且 zh-CN 的键集必须 ⊆ zh-Moe（en-US 的键是英文原文，
// 值可以是空串）。删会话那批新键单独点名核对 —— 它们跨了 index.html / app.js / 语言文件三处，
// 最容易出现"改了 JS 忘了加键"。
import { readFileSync } from "node:fs";

const dir = process.env.SIP_LANG_DIR;
if (!dir) { console.error("需要 SIP_LANG_DIR"); process.exit(2); }

const L = {};
let bad = 0;
for (const f of ["zh-CN", "en-US", "zh-Moe"]) {
  try {
    L[f] = JSON.parse(readFileSync(dir + "\\" + f + ".json", "utf8"));
    console.log(`  OK   ${f}.json  (${Object.keys(L[f]).length} 键)`);
  } catch (e) {
    console.log(`  BAD  ${f}.json  ${e.message}`);
    bad++;
  }
}
if (bad) process.exit(1);

const cn = Object.keys(L["zh-CN"]), moe = Object.keys(L["zh-Moe"]);
// 只查一个方向：zh-CN ⊆ zh-Moe。反方向**故意不算问题** ——
// 译文文件里可能有已废弃的旧键（见 tests/Sip.Tests/LangParityTests.cs 的注释）。
// 这里曾经把方向写反，把 zh-Moe 独有的键报成了失败。
const missing = cn.filter(k => !(k in L["zh-Moe"]));
const moeOnly = moe.filter(k => !(k in L["zh-CN"]));
console.log(`  zh-CN 有而 zh-Moe 没有的: ${missing.length ? missing.join(", ") : "(无)  ← 这才是失败项"}`);
console.log(`  zh-Moe 有而 zh-CN 没有的: ${moeOnly.length ? moeOnly.join(", ") : "(无)"}  ← 允许（旧键/单边补充）`);

const wanted = [
  "Delete",
  "this chat",
  "all chats for this book",
  "This cannot be undone.",
  "Chat history deleted",
  "Could not delete the chat history",
  "Delete this book's chat history (cannot be undone)",
];
let miss = 0;
for (const k of wanted) {
  const a = k in L["zh-CN"], b = k in L["zh-Moe"], c = k in L["en-US"];
  const okAll = a && b && c;
  if (!okAll) miss++;
  console.log(`  ${okAll ? "OK  " : "MISS"}  ${k}   zh-CN=${a} zh-Moe=${b} en-US=${c}`);
}

const fails = missing.length + miss;
console.log(fails ? `\n语言闸门: ${fails} 处问题` : "\n语言闸门: 全部通过");
process.exit(fails ? 1 : 0);
