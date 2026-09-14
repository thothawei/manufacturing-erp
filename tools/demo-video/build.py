import sys
from pathlib import Path
sys.path.insert(0, str(Path(__file__).parent))
from render import *   # noqa

OUT = Path(sys.argv[1])
OUT.mkdir(parents=True, exist_ok=True)
DEMO = Path(__file__).resolve().parent / "data"


def real(tag):
    """讀回實際執行過的指令與它真正的輸出 —— 影片裡的每個數字都是這樣來的。"""
    cmd = (DEMO / f"{tag}.cmd").read_text(encoding="utf-8").strip()
    out = [l.rstrip() for l in (DEMO / f"{tag}.out").read_text(encoding="utf-8").rstrip().split("\n")]
    return cmd, out


def scene(tag, title, subtitle, caption, hold=36):
    cmd, out = real(tag)
    return dict(title=title, subtitle=subtitle, command=cmd, output=out, caption=caption, hold=hold)


SCENES = [
    scene("01", "可用庫存，不是帳上庫存", "GET /api/items/{code}/inventory",
          "帳上 100 片、保留 20 片 → 可用只有 80 片。用帳上庫存回答「還有多少能用」會系統性偏樂觀。"),
    scene("02", "最多能做幾台：多階 BOM 展開", "GET /api/items/{code}/sufficiency",
          "40 台是後端展開 BOM 算出來的。basis 欄位標明它以可用庫存為準，不是帳上庫存。"),
    scene("03", "風險工單：兩種不同的風險來源", "GET /api/work-orders/at-risk",
          "兩張都延遲 2 天，原因完全不同：一張已逾期，一張是補料前置期趕不上交期。", hold=40),
    scene("04", "MRP：建議採購量不是缺料量", "GET /api/mrp/shortages",
          "淨缺 120 片，建議下單 150 片 —— 套用了訂購倍量 50。在途那 30 片趕不上需求日，不算供給。", hold=40),
    scene("05", "時間分期：什麼時候開始缺", "GET /api/mrp/time-phased",
          "上一個端點只說「總共缺多少」。這裡說的是第 1 週就見底，第 2 週到貨也只補到 -90。", hold=40),
    scene("06", "延遲風險：規則式 vs 機器學習", "GET /api/work-orders/{no}/delay-risk",
          "規則式說得出「為什麼」，模型給機率但說不出理由。兩邊並存 —— 訓練資料是模擬的，文件裡寫明了。",
          hold=42),
]

test_lines = [l.rstrip() for l in (DEMO / "tests.txt").read_text(encoding="utf-8").strip().split("\n")]
SCENES.append(dict(
    title="387 個測試，0 警告",
    subtitle="dotnet test --configuration Release",
    command="dotnet test --configuration Release",
    output=test_lines + ["", "# 另有 30 個檢索品質測試接真實 Ollama、3 個接真實 Anthropic API"],
    caption="每條防線都做過反向驗證：把防線拔掉、確認測試會紅，再還原。沒有紅過的測試等於沒有測試。",
    hold=42,
))

frames = []
frames += [("card", card(
    ["製造業 ERP + AI 助理",
     "Clean Architecture ・ 12 個 AI 工具 ・ 本機 RAG ・ ONNX 延遲風險模型"],
    sub="所有數字都由後端算好，LLM 只負責理解問題與選工具"))] * int(FPS * 3.4)

scene_states = [(s, render_scene(s, 0, 0, None)) for s in SCENES]
total = sum(len(st) for _, st in scene_states)

done = 0
for s, states in scene_states:
    for st in states:
        done += 1
        frames.append(("scene", s, st, done / total))

frames += [("card", card(
    ["誠實是這份作品集的一部分",
     "未驗證的環節、簡化的假設、模擬的資料，都寫在 README 裡"],
    sub="docs/one-pager.md ・ docs/demo-and-design-notes.md", accent=GREEN))] * int(FPS * 4)

print(f"總影格 {len(frames)}，約 {len(frames)/FPS:.1f} 秒")

for i, item in enumerate(frames):
    img = item[1] if item[0] == "card" else draw_scene_frame(item[1], item[2], item[3])
    img.save(OUT / f"f{i:05d}.png")
    if i % 250 == 0:
        print(f"  {i}/{len(frames)}")
print("done")
