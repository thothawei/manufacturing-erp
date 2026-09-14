# 展示影片的產生方式

`docs/images/demo.gif`（README 內嵌）與 `docs/videos/demo.mp4` 都是這裡產生的。

**影片裡每一個數字都是真的跑出來的。** `data/` 下的 `*.cmd` 是實際執行過的指令、
`*.out` 是它真正的輸出（啟動 API 之後打真實端點取得），渲染腳本只負責把它們畫成畫面。
沒有任何一段輸出是為了好看而手寫的 —— 這件事比影片本身重要，
因為一份展示如果連數字都能編，其他敘述也就沒有可信度了。

## 重新產生

```bash
# 一、啟動 API（另一個終端機）
dotnet run --project src/Erp.Api --urls http://localhost:5199

# 二、重新擷取真實輸出（data/ 下的 .cmd 就是這些指令）
cd tools/demo-video
for f in data/*.cmd; do eval "$(cat "$f")" > "${f%.cmd}.out"; done
dotnet test --configuration Release 2>&1 | grep "已通過!" | sed 's/持續時間.*//' > data/tests.txt

# 三、渲染影格並合成
python3 build.py /tmp/frames
ffmpeg -y -framerate 15 -i /tmp/frames/f%05d.png \
  -c:v libx264 -crf 20 -preset slow -pix_fmt yuv420p -movflags +faststart \
  ../../docs/videos/demo.mp4

# 四、GIF（README 內嵌用；兩段式 palette，直接轉會很醜）
ffmpeg -y -i ../../docs/videos/demo.mp4 \
  -vf "fps=12,scale=1000:-1:flags=lanczos,palettegen=max_colors=160:stats_mode=diff" /tmp/palette.png
ffmpeg -y -i ../../docs/videos/demo.mp4 -i /tmp/palette.png \
  -filter_complex "fps=12,scale=1000:-1:flags=lanczos[x];[x][1:v]paletteuse=dither=bayer:bayer_scale=3:diff_mode=rectangle" \
  ../../docs/images/demo.gif
```

需要 Python 的 Pillow（`pip install Pillow`）與 ffmpeg。字型用 macOS 內建的
Menlo（等寬）與 STHeiti（中文），換平台要改 `render.py` 最上面那兩個路徑。

## 為什麼不是螢幕錄影

螢幕錄影會把整個桌面錄進去，而且沒辦法保證「畫面上的輸出與指令真的對得上」——
剪接過的錄影誰也說不準中間發生了什麼。這裡的做法是先執行、把指令與輸出成對存檔，
再逐幀渲染：任何人都可以拿 `data/` 下的檔案去對照，或者自己重跑一次驗證。
