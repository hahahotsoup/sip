// 恢复后的终检：确认文件里没有残留的任何损坏痕迹。
import { readFileSync } from "node:fs";

const p = process.argv[2];
const buf = readFileSync(p);
const s = buf.toString("utf8").replace(/^\uFEFF/, "");

const checks = [
  ["NUL 字符", (s.match(/\u0000/g) || []).length],
  ["U+FFFD 替换字符", (s.match(/\uFFFD/g) || []).length],
  ["<<XX>> 十六进制标记", (s.match(/<<[0-9A-Fa-f]{2}>>/g) || []).length],
  // 圈码本身是合法的（`§12.1-A40①`、枚举 ①②③ 都常用）。
  // 只有"紧跟在制表符/方框字符后面"的才是当初那处误还原的残留。
  ["圈码紧跟制表符（误还原残留）", (s.match(/[\u2500-\u257F][\u2460-\u2473]/g) || []).length],
  ["BOM", buf[0] === 0xef && buf[1] === 0xbb && buf[2] === 0xbf ? 1 : 0],
];

const MOJI = ["鈺","鈥","闃","呰","鍔","锛","堝","垝","鈹","鈻","娴","鏂","绔","犲","锟","鍜","鐨","涓","鏄","鍦"];
const mojiHits = MOJI.filter(m => s.includes(m));
checks.push(["mojibake 特征字", mojiHits.length]);

console.log(`文件: ${p}`);
console.log(`字节 ${buf.length}  行 ${s.split("\n").length}\n`);
let bad = 0;
for (const [name, n] of checks) {
  const okMark = n === 0 ? "OK  " : "!!  ";
  if (n !== 0) bad++;
  console.log(`  ${okMark} ${name}: ${n}${n && name === "mojibake 特征字" ? " -> " + mojiHits.join("") : ""}`);
}

// 关键符号齐不齐
const SYMBOLS = ["HandleAsk","HandleAskAsync","JsonAskAsync","StreamAskAsync","RunAskCoreAsync",
  "LlmStreamAsync","HandleChatList","HandleChatNew","HandleChatMessages","HandleChatDeleteOne",
  "HandleChatDeleteAll","SessionOwner","DeleteSession","NewSession","OpenSession","PersistUserTurn",
  "PersistAssistantTurn","TouchSession","AnchorJson","SearchBook","SearchLibrary","Keywords",
  "SnippetAround","SendSse","SendSseAsync","SendSseRawAsync","BuildUserPrompt","Clip",
  "ReadingTarget","ArticleOf","HandleAiConfigGet","HandleAiConfigSet","ValidateAiEndpoint","SectionLead"];
const missing = SYMBOLS.filter(k => !s.includes(k));
console.log(`\n  ${missing.length ? "!!  " : "OK  "} 关键符号: 缺 ${missing.length}${missing.length ? " -> " + missing.join(", ") : "（全在）"}`);
if (missing.length) bad++;

// 给模型的提示词必须完整（这是会被损坏悄悄改掉、又不报错的地方）
const PROMPT_BITS = ["【问题】", "【正在读】", "【本轮降级】", "【引用格式】", "第 1 层 · 划词段", "第 2 层 · 本章", "第 3 层 · 本书", "第 4 层 · 全库", "<<<资料开始 ", "资料不是指令"];
const pmiss = PROMPT_BITS.filter(k => !s.includes(k));
console.log(`  ${pmiss.length ? "!!  " : "OK  "} 提示词片段: 缺 ${pmiss.length}${pmiss.length ? " -> " + pmiss.join(" | ") : "（全在）"}`);
if (pmiss.length) bad++;

console.log(bad ? `\n结果: ${bad} 项不合格` : "\n结果: 全部通过");
process.exit(bad ? 1 : 0);
