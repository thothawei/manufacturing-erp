"""把真實跑出來的 demo 輸出渲染成終端機操作影片的影格。

所有 JSON 都是實際打 API 拿到的（/tmp/demo/*.json），沒有一個數字是編的。
"""
import json
import os
import sys
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

W, H = 1280, 720
FPS = 15
OUT = Path(sys.argv[1] if len(sys.argv) > 1 else "frames")
DEMO = Path(__file__).resolve().parent / "data"

BG = (13, 13, 20)
TERM_BG = (24, 24, 37)
TERM_BAR = (35, 35, 52)
FG = (205, 214, 244)
DIM = (108, 112, 134)
GREEN = (166, 227, 161)
YELLOW = (249, 226, 175)
BLUE = (137, 180, 250)
PINK = (245, 194, 231)
RED = (243, 139, 168)
SUB_BG = (20, 20, 30)

MONO = "/System/Library/Fonts/Menlo.ttc"
HAN = "/System/Library/Fonts/STHeiti Medium.ttc"

f_mono = ImageFont.truetype(MONO, 19)
f_mono_s = ImageFont.truetype(MONO, 16)
f_han = ImageFont.truetype(HAN, 19)
f_han_s = ImageFont.truetype(HAN, 16)
f_title = ImageFont.truetype(HAN, 30)
f_sub = ImageFont.truetype(HAN, 21)
f_big = ImageFont.truetype(HAN, 46)
f_mid = ImageFont.truetype(HAN, 24)


def is_han(ch):
    return ord(ch) > 0x2E80


def draw_mixed(d, xy, text, fill, mono, han):
    """逐字繪製：ASCII 用等寬、CJK 用黑體。回傳結束的 x。"""
    x, y = xy
    for ch in text:
        font = han if is_han(ch) else mono
        d.text((x, y), ch, font=font, fill=fill)
        x += font.getlength(ch)
    return x


def colour_for(line):
    s = line.strip()
    if s.startswith('"') and '":' in s:
        return None  # 需要逐段上色
    return FG


def draw_json_line(d, x, y, line):
    """key 一個顏色、值另一個顏色，讓 JSON 好讀。"""
    stripped = line.lstrip()
    indent = len(line) - len(stripped)
    x += indent * f_mono.getlength(" ")

    if '":' in stripped:
        key, _, rest = stripped.partition(":")
        x = draw_mixed(d, (x, y), key + ":", BLUE, f_mono, f_han)
        val = rest.strip()
        colour = YELLOW
        if val.startswith('"'):
            colour = GREEN
        elif val.rstrip(",") in ("true", "false", "null"):
            colour = PINK
        draw_mixed(d, (x + 6, y), " " + val, colour, f_mono, f_han)
    else:
        colour = FG
        if "已通過" in stripped or "通過:" in stripped:
            colour = GREEN
        elif stripped.startswith("//") or stripped.startswith("#"):
            colour = DIM
        draw_mixed(d, (x, y), stripped, colour, f_mono, f_han)


def base_frame(title, subtitle, progress):
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)

    # 標題
    if title:
        draw_mixed(d, (54, 34), title, FG, f_title, f_title)
    if subtitle:
        draw_mixed(d, (54, 78), subtitle, DIM, f_han_s, f_han_s)

    # 終端機外框
    d.rounded_rectangle([48, 118, W - 48, H - 108], radius=10, fill=TERM_BG)
    d.rounded_rectangle([48, 118, W - 48, 150], radius=10, fill=TERM_BAR)
    d.rectangle([48, 140, W - 48, 152], fill=TERM_BAR)
    for i, c in enumerate([(243, 139, 168), (249, 226, 175), (166, 227, 161)]):
        d.ellipse([70 + i * 22, 128, 82 + i * 22, 140], fill=c)

    # 進度條
    d.rectangle([0, H - 6, W, H], fill=(30, 30, 46))
    d.rectangle([0, H - 6, int(W * progress), H], fill=BLUE)
    return img, d


def render_scene(scene, index, total_frames_so_far, all_frames):
    """把一個場景展開成影格。"""
    title = scene["title"]
    subtitle = scene.get("subtitle", "")
    command = scene["command"]
    output_lines = scene["output"]
    caption = scene.get("caption", "")
    hold = scene.get("hold", 30)

    frames = []

    # 一、打字動畫
    typed = 0
    while typed <= len(command):
        frames.append(("type", typed, 0, caption if typed == len(command) else ""))
        typed += 3
    frames.append(("type", len(command), 0, ""))

    # 二、輸出逐行浮現
    for n in range(len(output_lines) + 1):
        frames.append(("out", len(command), n, ""))
        frames.append(("out", len(command), n, ""))

    # 三、停留（字幕出現）
    for _ in range(hold):
        frames.append(("out", len(command), len(output_lines), caption))

    return frames


def draw_scene_frame(scene, state, progress):
    kind, typed, shown, caption = state
    img, d = base_frame(scene["title"], scene.get("subtitle", ""), progress)

    x0, y0 = 72, 172
    line_h = 26

    prompt = "$ "
    d.text((x0, y0), prompt, font=f_mono, fill=GREEN)
    cmd_x = x0 + f_mono.getlength(prompt)
    shown_cmd = scene["command"][:typed]

    # 長指令折行，第二行起縮排對齊
    max_x = W - 96
    cx, cy = cmd_x, y0
    for ch in shown_cmd:
        font = f_han if is_han(ch) else f_mono
        w = font.getlength(ch)
        if cx + w > max_x:
            cx = cmd_x + f_mono.getlength("  ")
            cy += line_h
        d.text((cx, cy), ch, font=font, fill=FG)
        cx += w

    if typed < len(scene["command"]):
        d.rectangle([cx, cy + 2, cx + 9, cy + 20], fill=FG)

    y = cy + line_h + 8
    for line in scene["output"][:shown]:
        if y > H - 130:
            break
        draw_json_line(d, x0, y, line)
        y += line_h

    if caption:
        d.rectangle([48, H - 100, W - 48, H - 44], fill=SUB_BG)
        d.rectangle([48, H - 100, 54, H - 44], fill=YELLOW)
        draw_mixed(d, (72, H - 88), caption, FG, f_sub, f_sub)

    return img


def card(lines, sub=None, accent=BLUE):
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    y = H // 2 - (len(lines) * 34) - (30 if sub else 0)
    for i, line in enumerate(lines):
        font = f_big if i == 0 else f_mid
        w = sum(font.getlength(c) for c in line)
        draw_mixed(d, ((W - w) / 2, y), line, FG if i == 0 else DIM, font, font)
        y += 62 if i == 0 else 40
    if sub:
        w = sum(f_sub.getlength(c) for c in sub)
        d.rectangle([(W - w) / 2 - 18, y + 16, (W + w) / 2 + 18, y + 60], fill=(30, 30, 46))
        draw_mixed(d, ((W - w) / 2, y + 24), sub, accent, f_sub, f_sub)
    return img


def load(name, limit=None, keep=None):
    text = (DEMO / name).read_text(encoding="utf-8")
    lines = text.rstrip().split("\n")
    if keep:
        lines = [l for l in lines if any(k in l for k in keep) or l.strip() in "{}[]"]
    if limit:
        lines = lines[:limit]
    return lines
