#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""t5 的进程级黑盒驱动：隔离实例 + 真跑 sip.exe + 直查 sqlite + 合成夹具。

为什么不用 xunit/xr（实测理由，不是偏好）：
  · `dotnet test` 在本沙箱起不来 testhost；
  · `xr` 反射运行器不支持 `IClassFixture`，且同一条命令跑出过「含汇总」与「半截无汇总」
    两种结果（stdout 偶发截断），拿它当证据不稳；
  · `SipWebServer` 一类需要 HttpListener，本沙箱构造即失败。
本驱动绕开这三者：每次判定都产出 (命令, 退出码, stdout/stderr, sqlite 查询结果) 四元组，
可以整段贴进验收报告。

隔离手法与 tests/Sip.Tests/TestHost.cs 一致：把产品输出拷到独立临时目录
（数据目录 = exe 同级 readwithhotsoup/，产品没有覆写口），并用 SIP_SIMON_KEY_NAME
把凭据命名空间隔离到本实例；结束时会清掉本实例写进凭据库的条目。
绝不指向真实 readwithhotsoup/。

用法：
  python tests/verification/sip_driver.py selftest      # 证明驱动本身可用
  python tests/verification/sip_driver.py help          # 打印 sip 当前的全部命令
作为库用：
  from sip_driver import SipInstance, make_epub2, make_epub3_nav, make_bad_zip
"""
import json
import os
import random
import shutil
import sqlite3
import subprocess
import sys
import time
import zipfile

sys.stdout.reconfigure(encoding="utf-8")
sys.stderr.reconfigure(encoding="utf-8")

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
PRODUCT_SRC = os.environ.get(
    "SIP_PRODUCT_SRC", os.path.join(REPO, "tests", "Sip.Tests", "bin", "Release", "net10.0", "sip"))
SCRATCH_ROOT = os.path.abspath(os.environ.get("SIP_VERIFY_SCRATCH", os.path.join(HERE, "_scratch")))
# ⚠️ 必须是绝对路径：run() 会把子进程 cwd 设成实例目录，相对路径会被 sip 再拼一次 cwd → FILE_NOT_FOUND
# Agent 门工具：sip 自带的 agentok 要求「真实终端 + Web 密码」，无头沙箱里必然 rc=3，
# 于是 imports/回填 全部跑不动。这个工具照 TestHost.OpenAgentGate 的做法直接写凭据。
GATE_EXE = os.environ.get("SIP_GATE_EXE", os.path.join(HERE, "gate", "bin", "Release", "net10.0", "gate.exe"))


def _rand(n=8):
    return "".join(random.choice("abcdefghijklmnopqrstuvwxyz0123456789") for _ in range(n))


class SipInstance:
    """一个隔离的 sip 实例：独立 exe 目录 + 独立 readwithhotsoup/ + 独立凭据命名空间。"""

    def __init__(self, open_gate=True, product_src=None, scratch=None):
        self.src = product_src or PRODUCT_SRC
        if not os.path.isfile(os.path.join(self.src, "sip.exe")):
            raise FileNotFoundError(f"产品输出不在 {self.src}（先 dotnet build；或设 SIP_PRODUCT_SRC）")
        self.key = "verifier_" + _rand(10)
        self.root = os.path.join(scratch or SCRATCH_ROOT, "sip-" + _rand(8))
        os.makedirs(self.root, exist_ok=True)
        # robocopy 比 shutil.copytree 快一个量级（实测 731 MB / 1 s），退出码 0-7 都算成功
        r = subprocess.run(["robocopy", self.src, self.root, "/E", "/NFL", "/NDL", "/NJH", "/NJS", "/NP", "/MT:16"],
                           capture_output=True, text=True)
        if r.returncode > 7:
            raise RuntimeError(f"robocopy 失败 rc={r.returncode}: {r.stdout[-400:]}")
        self.exe = os.path.join(self.root, "sip.exe")
        self.data = os.path.join(self.root, "readwithhotsoup")
        self.db = os.path.join(self.data, "rss.db")
        self.chat_db = os.path.join(self.data, "chat.db")
        self._gate = None if open_gate else False
        if open_gate:
            self.open_gate()

    # ── 凭据命名空间 / Agent 门 ─────────────────────────────
    def _env(self):
        e = dict(os.environ)
        e["SIP_SIMON_KEY_NAME"] = self.key
        e["DOTNET_CLI_UI_LANGUAGE"] = "en"
        return e

    def open_gate(self):
        """非交互调用会被 Agent 门拦（rc=3）；借 tests/verification/gate 写凭据开门。
        凭据写进本实例专属命名空间（SIP_SIMON_KEY_NAME），cleanup 时删除。"""
        if not os.path.isfile(GATE_EXE):
            raise FileNotFoundError(f"gate.exe 不存在：{GATE_EXE}（先 dotnet build tests/verification/gate/Gate.csproj -c Release）")
        r = subprocess.run([GATE_EXE, "open", self.key], capture_output=True, text=True)
        self._gate = (r.returncode, r.stdout.strip(), r.stderr.strip())
        if r.returncode != 0:
            raise RuntimeError(f"开门失败：{r.stdout} {r.stderr}")
        # 回读确认：产品自己怎么说，比工具的自我报告可信
        rc, so, _ = self.run("--agentstatus", timeout=90)
        self._gate_verified = (rc == 0 and "已开启" in so)
        return self._gate

    # ── 跑命令 ──────────────────────────────────────────────
    def run(self, *args, timeout=180, stdin=None, cwd=None):
        p = subprocess.run([self.exe, *args], capture_output=True, text=True,
                           encoding="utf-8", errors="replace", env=self._env(),
                           input=stdin, timeout=timeout, cwd=cwd or self.root)
        return p.returncode, p.stdout or "", p.stderr or ""

    def run_json(self, *args, **kw):
        rc, so, se = self.run(*args, **kw)
        try:
            return rc, json.loads(so), se
        except Exception:
            return rc, None, (se or so)

    # ── 库直查 ──────────────────────────────────────────────
    def sql(self, query, params=(), path=None):
        # Pooling 由 sqlite3 无关；只读打开避免抢锁
        con = sqlite3.connect(f"file:{path or self.db}?mode=ro", uri=True, timeout=10)
        try:
            cur = con.execute(query, params)
            return cur.fetchall()
        finally:
            con.close()

    def sql_write(self, query, params=()):
        con = sqlite3.connect(self.db, timeout=10)
        try:
            con.execute(query, params)
            con.commit()
        finally:
            con.close()

    def tables(self, path=None):
        return [r[0] for r in self.sql("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name", path=path)]

    def columns(self, table, path=None):
        return [r[1] for r in self.sql(f"PRAGMA table_info({table})", path=path)]

    # ── AI 配置（把基址指到本地假 LLM） ─────────────────────
    def write_ai_config(self, llm_endpoint="http://127.0.0.1:8931/v1", llm_model="fake-model",
                        emb_endpoint="http://127.0.0.1:8931/v1", emb_model="fake-embed",
                        dims=768, extra=None):
        cfg = {
            "Embedding": {"Provider": "openai-compatible", "Model": emb_model,
                          "Dimensions": dims, "ApiEndpoint": emb_endpoint, "SearchThreshold": 0.7},
            "Llm": {"Provider": "openai-compatible", "Model": llm_model, "ApiEndpoint": llm_endpoint},
            "AllowPrivateNet": False,
            "Chunking": {"SizeTokens": 2000, "OverlapTokens": 200},
        }
        if extra:
            cfg.update(extra)
        os.makedirs(self.data, exist_ok=True)
        path = os.path.join(self.data, "ai_config.json")
        with open(path, "w", encoding="utf-8") as fh:
            json.dump(cfg, fh, ensure_ascii=False, indent=2)
        return path

    def sha256(self, rel):
        import hashlib
        p = rel if os.path.isabs(rel) else os.path.join(self.data, rel)
        if not os.path.exists(p):
            return None
        h = hashlib.sha256()
        with open(p, "rb") as fh:
            for chunk in iter(lambda: fh.read(1 << 20), b""):
                h.update(chunk)
        return h.hexdigest()

    # ── 清理 ────────────────────────────────────────────────
    def cleanup(self, keep_on_failure=False, failed=False):
        self._clear_credentials()
        if keep_on_failure and failed:
            print(f"[keep] {self.root}", file=sys.stderr)
            return
        shutil.rmtree(self.root, ignore_errors=True)

    def _clear_credentials(self):
        # gate 工具删 ktsu 写的 agent_ok_*；其余（挡位/主库认领）用 cmdkey 兜底
        if os.path.isfile(GATE_EXE):
            try:
                subprocess.run([GATE_EXE, "close", self.key], capture_output=True, text=True, timeout=60)
            except Exception:
                pass
        for k in (self.key, self.key + "_level", "agent_ok_" + self.key, "primary_db_" + self.key):
            subprocess.run(["cmdkey", "/delete:hotsoupreader:" + k], capture_output=True, text=True)

    def __enter__(self):
        return self

    def __exit__(self, exc_type, exc, tb):
        self.cleanup(failed=exc_type is not None)
        return False


# ═════════════ 合成夹具（不进仓库二进制；报告里如实标注"来自夹具"） ═════════════
XHTML = ('<?xml version="1.0" encoding="utf-8"?>\n'
         '<html xmlns="http://www.w3.org/1999/xhtml"><head><title>{t}</title></head>'
         '<body>{body}</body></html>')


def make_epub2(path, files, ncx_items, title="Probe Book 2", uid="urn:uuid:probe2"):
    """files: {name: bodyhtml}；ncx_items: [(label, src, [children...])] 递归。"""
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)

    def navpoints(items, depth=0):
        out = []
        for i, it in enumerate(items):
            label, src = it[0], it[1]
            kids = it[2] if len(it) > 2 else []
            pid = f"np{depth}_{i}_{_rand(4)}"
            inner = f'<navLabel><text>{label}</text></navLabel><content src="{src}"/>'
            if kids:
                inner += navpoints(kids, depth + 1)
            out.append(f'<navPoint id="{pid}" playOrder="{len(out)+1}">{inner}</navPoint>')
        return "".join(out)

    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("mimetype", "application/epub+zip", zipfile.ZIP_STORED)
        z.writestr("META-INF/container.xml",
                   '<?xml version="1.0"?><container version="1.0" '
                   'xmlns="urn:oasis:names:tc:opendocument:xmlns:container">'
                   '<rootfiles><rootfile full-path="OEBPS/content.opf" '
                   'media-type="application/oebps-package+xml"/></rootfiles></container>')
        items, spine = [], []
        for i, (name, body) in enumerate(files.items()):
            z.writestr("OEBPS/" + name, XHTML.format(t=os.path.basename(name), body=body))
            items.append(f'<item id="i{i}" href="{name}" media-type="application/xhtml+xml"/>')
            spine.append(f'<itemref idref="i{i}"/>')
        z.writestr("OEBPS/toc.ncx",
                   '<?xml version="1.0" encoding="utf-8"?>'
                   '<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">'
                   f'<head><meta name="dtb:uid" content="{uid}"/></head><docTitle><text>{title}</text></docTitle>'
                   f'<navMap>{navpoints(ncx_items)}</navMap></ncx>')
        z.writestr("OEBPS/content.opf",
                   '<?xml version="1.0" encoding="utf-8"?>'
                   '<package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="bid">'
                   f'<metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>{title}</dc:title>'
                   f'<dc:identifier id="bid">{uid}</dc:identifier><dc:language>zh</dc:language></metadata>'
                   f'<manifest><item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>'
                   f'{"".join(items)}</manifest><spine toc="ncx">{"".join(spine)}</spine></package>')
    return path


def make_epub3_nav(path, files, nav_items, title="Probe Book 3", uid="urn:uuid:probe3"):
    """EPUB3：OPF version=3.0 + properties="nav" 的 nav.xhtml（契约 A23 要求当场合成）。"""
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)

    def lis(items, depth=0):
        out = []
        for it in items:
            label, href = it[0], it[1]
            kids = it[2] if len(it) > 2 else []
            sub = f"<ol>{lis(kids, depth + 1)}</ol>" if kids else ""
            out.append(f'<li><a href="{href}">{label}</a>{sub}</li>')
        return "".join(out)

    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("mimetype", "application/epub+zip", zipfile.ZIP_STORED)
        z.writestr("META-INF/container.xml",
                   '<?xml version="1.0"?><container version="1.0" '
                   'xmlns="urn:oasis:names:tc:opendocument:xmlns:container">'
                   '<rootfiles><rootfile full-path="OEBPS/content.opf" '
                   'media-type="application/oebps-package+xml"/></rootfiles></container>')
        items, spine = [], []
        for i, (name, body) in enumerate(files.items()):
            z.writestr("OEBPS/" + name, XHTML.format(t=os.path.basename(name), body=body))
            items.append(f'<item id="i{i}" href="{name}" media-type="application/xhtml+xml"/>')
            spine.append(f'<itemref idref="i{i}"/>')
        z.writestr("OEBPS/nav.xhtml",
                   '<?xml version="1.0" encoding="utf-8"?>'
                   '<html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops">'
                   f'<head><title>目录</title></head><body><nav epub:type="toc" id="toc"><ol>{lis(nav_items)}</ol>'
                   '</nav></body></html>')
        z.writestr("OEBPS/content.opf",
                   '<?xml version="1.0" encoding="utf-8"?>'
                   '<package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="bid">'
                   f'<metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:title>{title}</dc:title>'
                   f'<dc:identifier id="bid">{uid}</dc:identifier><dc:language>zh</dc:language>'
                   '<meta property="dcterms:modified">2026-01-01T00:00:00Z</meta></metadata>'
                   f'<manifest><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/>'
                   f'{"".join(items)}</manifest><spine>{"".join(spine)}</spine></package>')
    return path


def make_bad_zip(path):
    """截断的 zip：契约 A24 的坏归档反例（父与子全集.epub 是天然样本）。"""
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("hello.txt", "x" * 500 + "not a real epub")
    data = open(path, "rb").read()
    with open(path, "wb") as fh:
        fh.write(data[: len(data) // 2])       # 砍掉中央目录
    return path


def make_zip_slip(path, entry_name="../../../evil_slip.txt", payload=b"pwned"):
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with zipfile.ZipFile(path, "w") as z:
        z.writestr(entry_name, payload)
    return path


def make_zip_bomb(path, out_mb=64, declared_name="bomb.bin"):
    """高压缩比条目：64 MB 零字节压进去只有几十 KB。"""
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        z.writestr(declared_name, b"\0" * (out_mb * 1024 * 1024))
    return path


def make_xxe_epub(path, entity_file=r"C:\Windows\win.ini"):
    """EPUB2 + 外部实体引用的 NCX（契约 A25 的 XXE 样本）。"""
    os.makedirs(os.path.dirname(os.path.abspath(path)), exist_ok=True)
    ncx = ('<?xml version="1.0"?>'
           f'<!DOCTYPE ncx [<!ENTITY xxe SYSTEM "file:///{entity_file}">]>'
           '<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">'
           '<head><meta name="dtb:uid" content="urn:uuid:xxe"/></head><docTitle><text>&xxe;</text></docTitle>'
           '<navMap><navPoint id="a" playOrder="1"><navLabel><text>&xxe;</text></navLabel>'
           '<content src="c1.xhtml"/></navPoint></navMap></ncx>')
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as z:
        z.writestr("mimetype", "application/epub+zip", zipfile.ZIP_STORED)
        z.writestr("META-INF/container.xml",
                   '<?xml version="1.0"?><container version="1.0" '
                   'xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles>'
                   '<rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>'
                   '</rootfiles></container>')
        z.writestr("OEBPS/c1.xhtml", XHTML.format(t="c1", body="<p>正文一</p>"))
        z.writestr("OEBPS/toc.ncx", ncx)
        z.writestr("OEBPS/content.opf",
                   '<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf" version="2.0" '
                   'unique-identifier="bid"><metadata xmlns:dc="http://purl.org/dc/elements/1.1/">'
                   '<dc:title>XXE Probe</dc:title><dc:identifier id="bid">urn:uuid:xxe</dc:identifier>'
                   '<dc:language>zh</dc:language></metadata><manifest>'
                   '<item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>'
                   '<item id="i0" href="c1.xhtml" media-type="application/xhtml+xml"/></manifest>'
                   '<spine toc="ncx"><itemref idref="i0"/></spine></package>')
    return path


# ═════════════════════════════ 自检 ═════════════════════════════
def selftest():
    out = {"productSrc": PRODUCT_SRC, "scratch": SCRATCH_ROOT, "ok": False, "steps": []}
    with SipInstance() as inst:
        out["instanceRoot"] = inst.root
        t0 = time.time()
        rc, so, se = inst.run("--help")
        out["steps"].append({"cmd": "sip --help", "rc": rc, "ms": int((time.time() - t0) * 1000),
                             "stdoutHead": so.strip().splitlines()[:3], "stderrHead": se.strip().splitlines()[:3]})
        out["dbExists"] = os.path.exists(inst.db)
        out["tables"] = inst.tables() if out["dbExists"] else []
        out["gate"] = {"rc": inst._gate[0] if inst._gate else None}
        # 合成一本最小的 EPUB2，验证夹具能被产品认（导入命令名以 --help 为准）
        epub = make_epub2(os.path.join(inst.root, "probe2.epub"),
                          {"c1.xhtml": "<h1>第一章</h1><p>甲</p>",
                           "c2.xhtml": "<h1>第二章</h1><p>乙</p>"},
                          [("第一章", "c1.xhtml"), ("第二章", "c2.xhtml")])
        out["fixtures"] = {"epub2": os.path.basename(epub), "sizeKB": int(os.path.getsize(epub) / 1024)}
        bc = os.path.join(inst.root, "bad.epub")
        make_bad_zip(bc)
        out["fixtures"]["badZip"] = os.path.basename(bc)
        out["fixtures"]["badZipZipfileIsZipfile"] = zipfile.is_zipfile(bc)
        e3 = make_epub3_nav(os.path.join(inst.root, "probe3.epub"),
                            {"a.xhtml": "<h1>甲</h1>", "b.xhtml": "<h1>乙</h1>"},
                            [("甲", "a.xhtml"), ("乙", "b.xhtml")])
        out["fixtures"]["epub3"] = os.path.basename(e3)
        b = make_zip_bomb(os.path.join(inst.root, "bomb.epub"), out_mb=64)
        out["fixtures"]["zipBombKB"] = int(os.path.getsize(b) / 1024)
        s = make_zip_slip(os.path.join(inst.root, "slip.epub"))
        out["fixtures"]["zipSlipEntries"] = zipfile.ZipFile(s).namelist()
        x = make_xxe_epub(os.path.join(inst.root, "xxe.epub"))
        out["fixtures"]["xxe"] = os.path.basename(x)
        # 老库样本：有 Items 行、没有章节数据（回填用例的输入）
        inst.sql_write("INSERT INTO Feeds (Id,Title,FeedUrl,LastCheckedAt) VALUES (1,'本地导入','local',NULL)")
        out["steps"].append({"cmd": "seed Feeds row", "rc": 0})
        # 关键闭环：Agent 门开着 -> 真导入 -> 真读目录（这两步不通，t5 全部维度都无从谈起）
        rc, so, se = inst.run("--import", epub, "--title", "Probe2", timeout=180)
        out["steps"].append({"cmd": "sip --import <synth epub2>", "rc": rc, "stdout": so.strip()[:120]})
        out["itemsAfterImport"] = inst.sql("SELECT Id,Title,Status FROM Items")
        rc2, toc, se2 = inst.run_json("--toc", "1", "--json", timeout=180)
        out["steps"].append({"cmd": "sip --toc 1 --json", "rc": rc2,
                             "chapters": (toc or {}).get("chapters") if isinstance(toc, dict) else None})
        out["gateVerifiedByProduct"] = getattr(inst, "_gate_verified", None)
        out["ok"] = (rc == 0 and out["dbExists"] and "Items" in out["tables"]
                     and out["gateVerifiedByProduct"] is True and len(out["itemsAfterImport"]) == 1)
    print(json.dumps(out, ensure_ascii=False, indent=2))
    return 0 if out["ok"] else 1


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "selftest"
    if cmd == "selftest":
        return selftest()
    if cmd == "help":
        with SipInstance() as inst:
            rc, so, se = inst.run("--help")
            print(f"rc={rc}\n{so}\n--- stderr ---\n{se}")
            return rc
    print(__doc__)
    return 3


if __name__ == "__main__":
    raise SystemExit(main())
