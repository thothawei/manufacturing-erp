#!/usr/bin/env python3
"""把 AI 助理的實際問答渲染成一張圖（docs/images/resume-ai-assistant.png）。

由 scripts/demo-ai-screenshot.sh 呼叫，不建議單獨執行 —— 它假設 ERP API 已經
起在 API_URL，而且那支 ERP 的 log 正在寫進 LOG_PATH。

圖上的每個數字都是這支腳本當場打 API 拿到的，工具名稱與耗時則是從 ERP 的
稽核 log 解析出來的，兩邊都不是寫死的字串。要改問題就改下面的 QUESTIONS。
"""
import html
import json
import os
import re
import subprocess
import sys
import time
import urllib.request

API_URL = os.environ.get("ERP_URL", "http://localhost:5199") + "/api/ai-assistant/ask"
LOG_PATH = os.environ["LOG_PATH"]
OUT_PATH = os.environ.get("OUT_PATH", "docs/images/resume-ai-assistant.png")
CHROME = os.environ.get(
    "CHROME", "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"
)

QUESTIONS = [
    "面板還有多少可以用？",
    "TV-100 現在最多能做幾台？",
    "有哪些工單有延遲風險？",
    "未來一個月有什麼會缺料？要買多少？",
]

# ERP 的稽核 log 長這樣：
#   工具呼叫 get_item_inventory_status 完成，成功：True，耗時 259 ms，參數：{"item_code":"PANEL-01"}
TOOL_LINE = re.compile(
    r"工具呼叫 (?P<name>\S+) 完成，成功：(?P<ok>\w+)，耗時 (?P<ms>\d+) ms，參數：(?P<args>.*)"
)


def ask(question):
    """打一次 API，回傳 (答案, 這次請求期間 log 裡新出現的工具呼叫)。"""
    with open(LOG_PATH, encoding="utf-8", errors="replace") as f:
        f.seek(0, os.SEEK_END)
        mark = f.tell()

    body = json.dumps({"question": question}).encode()
    req = urllib.request.Request(
        API_URL, data=body, headers={"Content-Type": "application/json"}
    )
    with urllib.request.urlopen(req, timeout=90) as resp:
        payload = json.load(resp)
    answer = payload.get("answer") or payload.get("detail") or ""

    with open(LOG_PATH, encoding="utf-8", errors="replace") as f:
        f.seek(mark)
        tools = [m.groupdict() for line in f if (m := TOOL_LINE.search(line))]
    return answer, tools


def render(turns):
    def tool_row(t):
        ok = "#22c55e" if t["ok"] == "True" else "#ef4444"
        return (
            f'<div class="tool"><span class="dot" style="background:{ok}"></span>'
            f'{html.escape(t["name"])} {html.escape(t["args"])}'
            f'<span class="ms"> · {t["ms"]} ms</span></div>'
        )

    blocks = "\n".join(
        f'<div class="turn"><div class="q"><span>{html.escape(q)}</span></div>'
        + "".join(tool_row(t) for t in tools)
        + f'<div class="a">{html.escape(a)}</div></div>'
        for q, a, tools in turns
    )
    return f"""<!doctype html><html lang="zh-Hant"><head><meta charset="utf-8">
<style>
  *{{box-sizing:border-box;margin:0;padding:0}}
  body{{background:#0b1120;color:#e2e8f0;font-family:-apple-system,"PingFang TC","Helvetica Neue",sans-serif;padding:36px 44px}}
  .head{{display:flex;align-items:baseline;gap:14px;border-bottom:1px solid #1e293b;padding-bottom:14px;margin-bottom:24px}}
  .head h1{{font-size:21px;font-weight:600;letter-spacing:.3px}}
  .head .ep{{font-family:"SF Mono",Menlo,monospace;font-size:12.5px;color:#64748b}}
  .head .tag{{margin-left:auto;font-size:11.5px;color:#94a3b8;border:1px solid #334155;border-radius:99px;padding:3px 11px}}
  .turn{{margin-bottom:22px}}
  .q{{display:flex;justify-content:flex-end;margin-bottom:9px}}
  .q span{{background:#1d4ed8;color:#fff;padding:9px 15px;border-radius:14px 14px 3px 14px;font-size:14.5px;max-width:62%}}
  .a{{background:#111c31;border:1px solid #1e293b;border-radius:3px 14px 14px 14px;padding:13px 16px;max-width:88%;font-size:14px;line-height:1.75;white-space:pre-wrap}}
  .tool{{display:flex;align-items:center;gap:7px;margin-bottom:9px;font-family:"SF Mono",Menlo,monospace;font-size:11.5px;color:#7dd3fc}}
  .tool .dot{{width:6px;height:6px;border-radius:50%;flex:none}}
  .tool .ms{{color:#475569}}
  .foot{{margin-top:26px;padding-top:14px;border-top:1px solid #1e293b;font-size:11.5px;color:#64748b;line-height:1.7}}
  b{{color:#e2e8f0;font-weight:600}}
</style></head><body>
<div class="head">
  <h1>製造業 ERP · AI 助理</h1>
  <span class="ep">POST /api/ai-assistant/ask</span>
  <span class="tag">LLM 只挑工具，數字全由後端計算</span>
</div>
{blocks}
<div class="foot">
  <b>怎麼運作</b>：使用者問一句自然語言 → LLM 從 12 個工具裡挑一個並填參數 → 後端服務查資料庫並完成所有計算 → 結果回給 LLM 組成回答。<b>模型不做任何算術</b>，畫面上每個數字都來自上方標示的那次工具呼叫（耗時取自系統稽核日誌）。<br>
  <b>寫入要人工確認</b>：12 個工具中 11 個唯讀；唯一會寫入的 suggest_purchase_order 只產生「待確認的採購建議」，須經人工核准端點才會成立正式採購單。
</div>
</body></html>"""


def shoot(html_path):
    """截圖。headless Chrome 截完不一定會自己退出，所以檔案一出現就收掉它。"""
    if os.path.exists(OUT_PATH):
        os.remove(OUT_PATH)
    proc = subprocess.Popen(
        [CHROME, "--headless", "--disable-gpu", "--hide-scrollbars",
         "--user-data-dir=/tmp/erp-ai-demo-chrome", "--window-size=1200,1400",
         "--force-device-scale-factor=2", "--virtual-time-budget=3000",
         f"--screenshot={OUT_PATH}", f"file://{html_path}"],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    try:
        for _ in range(60):
            if os.path.exists(OUT_PATH) and os.path.getsize(OUT_PATH) > 0:
                time.sleep(1)  # 等它把檔案寫完
                return True
            if proc.poll() is not None:
                break
            time.sleep(1)
    finally:
        proc.terminate()
        try:
            proc.wait(timeout=10)
        except subprocess.TimeoutExpired:
            proc.kill()
    return os.path.exists(OUT_PATH) and os.path.getsize(OUT_PATH) > 0


def crop_bottom(path):
    """裁掉底部空白。沒裝 Pillow 就跳過 —— 圖還是能用，只是下面留白。"""
    try:
        from PIL import Image
    except ImportError:
        print("→ 沒有 Pillow，略過裁切")
        return
    im = Image.open(path).convert("RGB")
    w, h = im.size
    bg = im.getpixel((5, 5))
    px = im.load()
    last = 0
    for y in range(h):
        for x in range(0, w, 4):
            c = px[x, y]
            if sum(abs(c[i] - bg[i]) for i in range(3)) > 18:
                last = y
                break
    im.crop((0, 0, w, min(h, last + 72))).save(path)


def main():
    turns = []
    for q in QUESTIONS:
        answer, tools = ask(q)
        if not tools:
            print(f"✗ 「{q}」沒有觸發任何工具，答案：{answer[:80]}", file=sys.stderr)
            return 1
        print(f"✓ {q} → {', '.join(t['name'] for t in tools)}")
        turns.append((q, answer, tools))

    tmp = "/tmp/erp-ai-demo.html"
    with open(tmp, "w", encoding="utf-8") as f:
        f.write(render(turns))

    if not shoot(tmp):
        print("✗ Chrome 沒有產出截圖", file=sys.stderr)
        return 1
    crop_bottom(OUT_PATH)
    print(f"✓ 已產出 {OUT_PATH}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
