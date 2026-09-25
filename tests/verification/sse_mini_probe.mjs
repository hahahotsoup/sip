// 最小实验：HttpListener 写 chunked + 每帧 Flush，客户端能不能**增量**收到？
// 服务端与客户端各打时间戳，事后对齐 —— 用来分清是"服务端没发出去"还是"客户端读法问题"。
import { spawn, spawnSync } from "node:child_process";
import { writeFileSync, readFileSync, existsSync, mkdirSync, rmSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { createConnection } from "node:net";

const DIR = join(tmpdir(), "sse-mini");
rmSync(DIR, { recursive: true, force: true });
mkdirSync(DIR, { recursive: true });
const D = DIR.replace(/\\/g, "\\\\");

const SRC = `
using System;using System.Diagnostics;using System.IO;using System.Net;using System.Net.Sockets;
using System.Text;using System.Threading.Tasks;
class P{
  static int Free(){var t=new TcpListener(IPAddress.Loopback,0);t.Start();int p=((IPEndPoint)t.LocalEndpoint).Port;t.Stop();return p;}
  static async Task Main(){
    int port=Free();
    var log=new StringBuilder();
    var l=new HttpListener();
    l.Prefixes.Add("http://127.0.0.1:"+port+"/");
    l.Start();
    File.WriteAllText(@"${D}\\port.txt", port.ToString());
    var ctx=await l.GetContextAsync();
    var res=ctx.Response;
    res.StatusCode=200; res.ContentType="text/event-stream; charset=utf-8";
    res.SendChunked=true;
    var sw=Stopwatch.StartNew();
    for(int i=0;i<6;i++){
      var b=Encoding.UTF8.GetBytes("event: delta\\ndata: {\\"text\\":\\"c"+i+"\\"}\\n\\n");
      res.OutputStream.Write(b,0,b.Length);
      res.OutputStream.Flush();
      log.Append("server wrote "+i+" at "+sw.ElapsedMilliseconds+"ms\\n");
      await Task.Delay(300);
    }
    res.Close();
    File.WriteAllText(@"${D}\\server.log", log.ToString());
    l.Stop();
  }
}`;

writeFileSync(join(DIR, "P.cs"), SRC);
writeFileSync(join(DIR, "P.csproj"),
  '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType>' +
  '<TargetFramework>net10.0</TargetFramework><Nullable>disable</Nullable>' +
  '<ImplicitUsings>disable</ImplicitUsings></PropertyGroup></Project>');

const b = spawnSync("dotnet", ["build", DIR, "-c", "Release", "--nologo", "-v", "q"], { encoding: "utf8" });
if (b.status !== 0) { console.log("build failed:\n" + (b.stdout || "") + (b.stderr || "")); process.exit(1); }

const child = spawn("dotnet", [join(DIR, "bin", "Release", "net10.0", "P.dll")], {
  stdio: ["ignore", "pipe", "pipe"],
});
let srvErr = "";
child.stderr.on("data", (d) => { srvErr += d.toString(); });
child.stdout.on("data", (d) => { srvErr += d.toString(); });
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

// 等服务端写 port.txt
let port = null;
for (let i = 0; i < 60; i++) {
  if (existsSync(join(DIR, "port.txt"))) { port = readFileSync(join(DIR, "port.txt"), "utf8").trim(); break; }
  await wait(200);
}
if (!port) { console.log("服务端没起来"); child.kill(); process.exit(1); }
console.log("服务端端口: " + port);

// 裸 socket 读 —— 完全绕开 HttpClient，任何缓冲都只可能是服务端/内核的
const t0 = Date.now();
const arrivals = [];
const sock = createConnection({ host: "127.0.0.1", port }, () => {
  sock.write(`GET / HTTP/1.1\r\nHost: 127.0.0.1:${port}\r\nAccept: text/event-stream\r\nConnection: keep-alive\r\n\r\n`);
});
sock.on("data", (buf) => {
  const chunkAt = Date.now() - t0;
  const s = buf.toString("utf8");
  const n = (s.match(/event: delta/g) || []).length;
  arrivals.push({ at: chunkAt, bytes: buf.length, frames: n, head: s.slice(0, 40).replace(/\r?\n/g, "|") });
});
sock.on("error", (e) => { console.log("socket 错误: " + e.code); });
await wait(4000);
sock.destroy(); child.kill();

if (srvErr.trim()) {
  console.log("\n=== 服务端 stderr/stdout ===");
  srvErr.split("\n").filter((l) => l.trim()).slice(0, 12).forEach((l) => console.log("  " + l));
}

console.log("\n=== 客户端收到的每一批数据 ===");
arrivals.forEach((a) => console.log(`  ${String(a.at).padStart(5)}ms  ${String(a.bytes).padStart(4)} 字节  帧数=${a.frames}  «${a.head}»`));
console.log("  共 " + arrivals.length + " 批");

console.log("\n=== 服务端自报的写出时刻 ===");
if (existsSync(join(DIR, "server.log"))) {
  readFileSync(join(DIR, "server.log"), "utf8").split("\n").filter((l) => l.trim()).forEach((l) => console.log("  " + l));
} else console.log("  （没写出来 —— 服务端可能没收到请求）");

const batched = arrivals.length <= 2 && arrivals.reduce((s, a) => s + a.frames, 0) >= 4;
console.log("\n结论: " + (batched
  ? "❌ 增量失败 —— 服务端每帧 Flush 了，客户端仍一次性收齐（服务端/内核缓冲）"
  : "✅ 增量成立 —— 客户端分批收到"));
