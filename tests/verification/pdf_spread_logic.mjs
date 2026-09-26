// 对开双页的页码逻辑检查。
//
// 关键点：**断言的是 app.js 里那几个函数的原文**，不是另写一份副本 ——
// 复制一份来测只能证明"副本是对的"，改坏了 app.js 它照样绿。
// 抽取方式见下方 sliceFn()：按函数名从源码里切出定义，配一个最小 state 跑。
//
// 跑法：node tests/verification/pdf_spread_logic.mjs
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

const here = dirname(fileURLToPath(import.meta.url));
const appJs = readFileSync(join(here, "..", "..", "web", "app.js"), "utf8");

/** 从源码里切出一个函数的定义原文。
 *  用花括号配对找结尾，而不是靠正则匹配到行尾 —— 这几个函数里本来就有嵌套的 {} 与模板串。
 *  ⚠️ 必须要求 "function X(" **在行首**：app.js 顶部的说明注释里就写着
 *  `function pdfSpreadOn` 这些字，只按 indexOf 找会抽到注释里那一行（实测踩过：
 *  抽出来的是一个只有函数签名的残片，于是断言全都在验空气）。 */
function sliceFn(name) {
  const marker = `\nfunction ${name}(`;
  const at = appJs.indexOf(marker);
  if (at < 0) throw new Error(`app.js 里找不到行首的 function ${name}()`);
  const start = at + 1;
  let i = appJs.indexOf("{", start);
  let depth = 0;
  for (let j = i; j < appJs.length; j++) {
    if (appJs[j] === "{") depth++;
    else if (appJs[j] === "}") {
      depth--;
      if (depth === 0) return appJs.slice(start, j + 1);
    }
  }
  throw new Error(`${name} 的函数体没有闭合`);
}

const src = ["pdfSpreadOn", "pdfAnchorOf", "pdfSpreadPages", "pdfStep", "setPdfSpread"]
  .map(sliceFn)
  .join("\n\n");

// 最小替身：只提供这几个函数真正用到的东西
const shim = `
const state = { pdfSpread: false, ebookPage: 1, ebookPages: 5, ebookMeta: { isPdf: true }, ebookId: 7, view: "ebook" };
const PREF = {};
function savePrefs(p) { Object.assign(PREF, p); }
let toastMsg = "";
function toast(m) { toastMsg = m; }
let rendered = 0;
function render() { rendered++; }
const progresses = [];
function api(path, opts) { progresses.push(JSON.parse(opts.body).position); return Promise.resolve(); }
`;

const mod = new Function(`${shim}\n${src}\nreturn { state, PREF, pdfSpreadOn, pdfAnchorOf, pdfSpreadPages, pdfStep, setPdfSpread, progresses, get rendered(){return rendered;} };`)();

let pass = 0, fail = 0;
function eq(actual, expected, what) {
  const a = JSON.stringify(actual), e = JSON.stringify(expected);
  if (a === e) { pass++; console.log(`  ok   ${what} → ${a}`); }
  else { fail++; console.log(`  FAIL ${what}\n        实际 ${a}\n        期望 ${e}`); }
}

const { state } = mod;

console.log("— 单页模式（默认）—");
eq(state.pdfSpread, false, "默认不是对开");
eq(mod.pdfStep(), 1, "步长 1");
for (const p of [1, 2, 3, 4, 5]) eq(mod.pdfAnchorOf(p), p, `第 ${p} 页的锚点 = 它自己`);
state.ebookPage = 3;
// 注意：单页模式这一支恒为 [1]（"这一屏显示第 1 页"）——真实渲染在 `m.isPdf` 分支里
// 只在 spread 打开时才调它，所以这里断言的是**函数契约**，不是"单页时也返回 3"
eq(mod.pdfSpreadPages(3), [1], "单页模式：这一屏就是第 1 页");

console.log("\n— 切到对开（保留当前页）—");
state.ebookPage = 4;
await mod.setPdfSpread(true, true);   // 它内部会**异步**上报进度：不 await 就查不到
eq(state.pdfSpread, true, "已开启");
eq(mod.PREF.pdfSpread, true, "偏好已存");
eq(state.ebookPage, 3, "第 4 页所属跨页的锚点 = 3");
eq(mod.rendered, 1, "重绘了一次");
eq(mod.progresses, [3], "进度按**锚点**上报（不是原页码 4）");

console.log("\n— 对开的页码对齐（第 1 页单独当封面）—");
eq(mod.pdfStep(), 2, "步长 2");
eq(mod.pdfAnchorOf(1), 1, "封面锚点 1");
eq(mod.pdfAnchorOf(2), 1, "第 2 页归到 1（封面是单独一屏）");
eq(mod.pdfAnchorOf(3), 3, "第 3 页锚点 3");
eq(mod.pdfAnchorOf(4), 3, "第 4 页归到 3");
eq(mod.pdfAnchorOf(5), 5, "第 5 页锚点 5");
eq(mod.pdfSpreadPages(1), [1], "封面单独一屏");
eq(mod.pdfSpreadPages(3), [3, 4], "3 与 4 并排");
eq(mod.pdfSpreadPages(4), [3, 4], "从第 4 页进来也落在 3-4 这一屏");
eq(mod.pdfSpreadPages(5), [5], "末页是奇数 → 右半页留空（只回来一页）");

console.log("\n— 翻屏步长：一屏两页，不是一屏半页 —");
eq(state.ebookPages, 5, "本 PDF 共 5 页");
state.ebookPage = 3;
// 模拟 ← / → 走的是 ebookGo(±step)，这里直接验锚点算术（ebookGo 还会 render+报进度）
eq(mod.pdfAnchorOf(state.ebookPage + mod.pdfStep()), 5, "3 → 5（一次一屏）");
eq(mod.pdfAnchorOf(state.ebookPage - mod.pdfStep()), 1, "3 → 1");
// 末屏判断：nextA === cur 就该禁用"下一屏"
eq(mod.pdfAnchorOf(5 + mod.pdfStep()), 5, "5 → 5 = 到头了（末页奇数那屏没有下一页）");

console.log("\n— 回到单页：仍停在当前这一页 —");
state.ebookPage = 5;
mod.setPdfSpread(false, true);
eq(state.pdfSpread, false, "已关闭");
eq(state.ebookPage, 5, "停在第 5 页，没被打回开头");
eq(mod.pdfStep(), 1, "步长回到 1");

console.log("\n— 偶数总页数（末屏是完整的一对）—");
state.ebookPages = 6;
mod.setPdfSpread(true, true);
state.ebookPage = 5;
eq(mod.pdfSpreadPages(5), [5, 6], "5-6 并排");
eq(mod.pdfAnchorOf(6), 5, "第 6 页归到 5");
eq(mod.pdfAnchorOf(5 + mod.pdfStep()), 5, "5 → 5 = 到头");

console.log(`\n${fail === 0 ? "全部通过" : "有失败"}：${pass} 通过 / ${fail} 失败`);
process.exit(fail === 0 ? 0 : 1);
