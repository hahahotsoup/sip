/* 本文件是 login.html 旧内联 <script> 块的外部孪生版本（CSP script-src 'self' 禁止内联脚本，故原样外置，由内置服务器以 /login.js 提供）。 */
/* 本地句子池。原来这里调的是外网一言（v1.hitokoto.cn）：每次打开登录页、
   以及之后每 20 秒一次，都会把「这台机器正在打开 sip 登录页」告诉第三方 ——
   与本项目「不收集、不上传」的立场直接冲突，登录页尤其不该有出网请求。
   句子全部取自 sip 自己的文案（品牌语/标语），想换随意增删这里即可。 */
const QUOTES = [
  { text: "慢慢来，不着急。", from: "sip" },
  { text: "品，你细品。", from: "sip" },
  { text: "本地优先的个人信息库。", from: "sip" },
  { text: "local · quiet · yours", from: "sip" },
];

const $ = (id) => document.getElementById(id);

function toast(msg) {
  const el = $("toast");
  el.textContent = msg;
  el.classList.add("show");
  clearTimeout(el._t);
  el._t = setTimeout(() => el.classList.remove("show"), 1800);
}

function loadQuote() {
  const box = $("quote");
  const q = QUOTES[Math.floor(Math.random() * QUOTES.length)];
  $("quoteText").textContent = q.text;
  $("quoteAuthor").textContent = q.from ? "—— " + q.from : "—— sip";
  box.classList.remove("is-swap");
  void box.offsetWidth; /* reflow 重启动画 */
  box.classList.add("is-swap");
}

$("quoteMore").addEventListener("click", loadQuote);
/* 这里原来有一行 `$("forgot").addEventListener(...)`，但页面里**没有 id="forgot" 的元素**
   —— 对 null 调 addEventListener 会抛 TypeError，而这行在**注册下面登录表单之前**，
   于是整个登录页点不动：设了密码之后根本登不进去。
   密码重置本来就只能走 `sip webpass`（需要真实终端），所以这条死链直接删掉，
   而不是补一个点不动的按钮。 */

$("loginForm").addEventListener("submit", async (e) => {
  e.preventDefault();
  const password = $("pass").value;
  if (!password) { toast("请输入密码"); return; }
  const btn = e.target.querySelector(".btn-login");
  btn.disabled = true;
  try {
    const res = await fetch("/api/login", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      credentials: "same-origin",
      body: JSON.stringify({ password }),
    });
    const data = await res.json().catch(() => ({}));
    if (data.success) {
      setTimeout(() => location.replace("/"), 80);
      return;
    }
    toast(data.error?.message || "密码错误");
  } catch {
    toast("网络错误");
  } finally {
    btn.disabled = false;
  }
});

/* 首屏拉一句，之后每 20s 轻换（可点「一言」立刻换） */
loadQuote();
setInterval(loadQuote, 20000);
