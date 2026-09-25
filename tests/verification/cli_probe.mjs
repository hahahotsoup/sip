// sip 命令行测试台（可复用）
//
// 为什么需要它：验证 CLI 要一个**隔离实例**（不能碰用户的 readwithhotsoup/），
// 而每次手搭的代价很高 —— 复制一份构建产物 ≈700 MB，手工搭几次就把磁盘堆满了
// （本轮实测踩到过）。所以固定一个目录、每次原地重建。
//
// 用法：
//   node tests/verification/cli_probe.mjs reset          # 重建实例（清库、重拷产物）
//   node tests/verification/cli_probe.mjs run <args...>  # 在实例里跑 sip，打印 stdout/exit
//
// 两个必须知道的点（都是踩出来的）：
//   · Agent 门默认关：非真人终端调用 sip 会 rc=3。这里用 gate 工具开门（每实例独立 key）。
//   · **把输出接进管道也算程序调用** —— 所以 run 用文件重定向，再把内容读回来。
import { execFileSync, spawnSync } from "node:child_process";
import { cpSync, existsSync, mkdirSync, rmSync, readFileSync, openSync, closeSync, writeSync } from "node:fs";
import { join, resolve } from "node:path";

const ROOT = resolve(import.meta.dirname, "..", "..");
const PROBE = join(ROOT, ".probe");             // 仓库内、已 gitignore（见文件末尾）
const SIP_DIR = join(PROBE, "sip");             // 产品输出的副本
const KEY = "cli_probe";
const GATE = join(ROOT, "tests", "verification", "gate", "bin", "Release", "net10.0", "gate.exe");

function buildGate() {
  if (!existsSync(GATE)) {
    execFileSync("dotnet", ["build", join(ROOT, "tests", "verification", "gate"), "-c", "Release", "--nologo", "-v", "q"], { stdio: "inherit" });
  }
}

function reset() {
  rmSync(PROBE, { recursive: true, force: true });
  mkdirSync(SIP_DIR, { recursive: true });
  const src = join(ROOT, "bin", "Release", "net10.0");
  if (!existsSync(src)) throw new Error("先跑 dotnet build sip.csproj -c Release");
  cpSync(src, SIP_DIR, { recursive: true });
  buildGate();
  // 开门（隔离 key，用完即弃；不碰真实用户的凭据）
  spawnSync(GATE, ["open", KEY], { stdio: "ignore" });
  console.log("probe ready: " + SIP_DIR);
}

function run(args) {
  const sip = join(SIP_DIR, "sip.exe");
  if (!existsSync(sip)) throw new Error("先跑 reset");
  buildGate();
  spawnSync(GATE, ["open", KEY], { stdio: "ignore" });
  const outFile = join(PROBE, "_out.log");
  // 关键：**不能走管道** —— 把 sip 的输出接进管道会被它判定成"程序调用"，Agent 门直接 rc=3。
  // 所以把 stdout/stderr 两个 fd 都指向同一个文件，再用 Node 读回来。
  // （早先那版用 spawnSync("cmd", [...]) 做重定向，cmd 没能被解析成可执行文件，
  //   结果是静默地什么都没跑 —— 这也是它写在这里的原因。）
  const fd = openSync(outFile, "w");
  let code = -1;
  try {
    // SIP_SIMON_KEY_NAME 必须给：gate 开的是 `agent_ok_<KEY>`，
    // sip 也得以**同一个 key 作用域**去查那条凭据 —— 少了它两边对不上，
    // 症状就是"门明明开了，程序调用仍被拒"（rc=3）。
    const r = spawnSync(sip, args, {
      stdio: ["ignore", fd, fd],
      windowsHide: true,
      env: { ...process.env, SIP_SIMON_KEY_NAME: KEY },
    });
    code = r.status ?? -1;
    if (r.error) { writeSync(fd, "\n[spawn error] " + r.error.message + "\n"); }
  } finally { closeSync(fd); }
  const out = existsSync(outFile) ? readFileSync(outFile, "utf8") : "";
  return { code, out };
}

const [cmd, ...rest] = process.argv.slice(2);
if (cmd === "reset") { reset(); }
else if (cmd === "run") {
  const r = run(rest);
  process.stdout.write(r.out);
  console.log(`\n[exit: ${r.code}]`);
  process.exitCode = r.code === 0 ? 0 : 1;
} else {
  console.log("用法: reset | run <sip 参数...>");
  process.exitCode = 2;
}
