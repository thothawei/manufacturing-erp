# 線上展示站怎麼部署

一份 `Dockerfile`，兩個平台設定。本機已經驗證過容器跑得起來（見最後一節），
剩下的只有「在平台上按下部署」這一步 —— 那需要帳號，所以留給你自己做。

## 先決定：Render 還是 Fly.io

| | Render（建議） | Fly.io |
|---|---|---|
| 免費方案要綁卡 | **不用** | 要 |
| 閒置行為 | 15 分鐘後休眠，冷啟動約 40–60 秒 | 可設為停機自動喚醒，冷啟動較快 |
| 適合 | 履歷上的一個連結 | 需要隨時可用的展示 |

選 Render 的理由很實際：**不必綁卡**。會休眠是缺點，但比「忘記關掉被扣錢」好。
README 的連結旁邊寫明「第一次開啟要等幾十秒」就沒有問題了。

## Render

1. 到 <https://dashboard.render.com/blueprints> → New Blueprint Instance。
2. 指向這個 repo，它會讀 `deploy/render.yaml`。
3. 部署完成後拿到 `https://<name>.onrender.com`，把它填進 README 的「線上試用」那一行。

只想手動建立而不用 Blueprint 的話：New → Web Service → 選 Docker →
Dockerfile 路徑 `./Dockerfile`、Health Check Path `/health`，
環境變數照 `render.yaml` 的 `envVars` 填。

## Fly.io

```bash
fly launch --no-deploy --copy-config --config deploy/fly.toml
fly deploy --config deploy/fly.toml --dockerfile Dockerfile
```

## 這個環境下哪些功能不會動，為什麼

展示站跑的是 `ASPNETCORE_ENVIRONMENT=Demo`。它與 Development 做同樣兩件事
（開放 Scalar、啟動時建表並灌展示資料），但刻意是一個**獨立的環境名稱** ——
正式環境不該有這兩者，而「展示站需要它們」與「開發機需要它們」是兩個不同的理由。

| 功能 | 在展示站上 | 原因 |
|---|---|---|
| 查詢端點、MRP、時間分期、ML 延遲風險 | 都能用 | 不依賴任何外部服務 |
| Scalar 互動文件 `/scalar/v1` | 開放 | Demo 環境刻意開的 |
| AI 助理 `POST /api/ai-assistant/ask` | **回 503** | 刻意不放金鑰 —— 見下 |
| 文件檢索（RAG） | 回 `SERVICE_UNAVAILABLE` | 需要本機 Ollama，容器裡沒有 |

### BaseUrl 與 Model 要成對設定

`appsettings.json` 預設打的是**開發機上的 OmniRoute gateway**（`localhost:20128`），
模型代號也跟著是 gateway 的格式（`anthropic/claude-opus-5`，帶 provider 前綴）。
容器裡沒有那個 gateway，所以部署設定把兩個都改回官方端點的寫法。

只清 `BaseUrl` 不夠：官方端點不認得帶前綴的模型代號，有人在展示站設了金鑰
就會因為模型名稱失敗。實測確認過啟動 log 會印出生效的組合：

```
AI 助理設定：模型 claude-opus-5，端點 Anthropic 官方，工具迴圈上限 5 輪，逾時 60 秒，
API 金鑰來源：未設定（將交由 SDK 自行解析憑證，若無憑證會回 503）
```

### 為什麼公開展示站不放 Anthropic 金鑰

放上去等於把金鑰交給所有能打這個網址的人：每一次請求都花你的錢，
而且沒有任何速率限制擋得住有心人。端點會回 503 並說明原因，
那是誠實的行為，不是故障。

真的要開放 AI 問答的話，至少要先有：每 IP 的速率限制、每日花費上限、
以及一個能隨時關掉的開關。那是另一個工程，不在這個展示站的範疇裡。

## 資料會不會被弄壞

不會。SQLite 檔建在容器的 `/tmp` 裡，**每次啟動重建**展示資料，
而且十二個 AI 工具裡只有一個會寫入（寫的還是「待人工確認的採購建議」）。
訪客隨便打不會影響任何人。

## 本機先驗一次（部署前建議跑一遍）

```bash
docker build -t erp-demo .
docker run --rm -p 8088:8080 erp-demo

curl http://localhost:8088/                     # 展示站導覽
curl http://localhost:8088/health
curl http://localhost:8088/api/mrp/shortages
open http://localhost:8088/scalar/v1
```

實測結果（2026-09-14，Docker Desktop / aarch64）：映像 558 MB，
啟動到 `/health` 回 200 約 10 秒，ML 模型在 Linux 容器裡載得起來且
算出來的機率與本機 macOS 一致（0.6855952739715576）——
ONNX 跨平台這件事順便驗掉了。
