# 製造業 ERP 系統

[![CI](https://github.com/thothawei/manufacturing-erp/actions/workflows/ci.yml/badge.svg)](https://github.com/thothawei/manufacturing-erp/actions/workflows/ci.yml)

Clean Architecture 分層的製造業 ERP，含一個以 tool-use 驅動的 AI 助理：
使用者用自然語言提問，AI 透過十二個工具查詢系統資料後回答，所有數字都由後端算好。
其中十個是唯讀查詢，唯一會寫入的那個寫出來的是「待人工確認的採購建議」，不是採購單。
最後一個工具是本機向量檢索（RAG），在 SOP、維修手冊與客訴紀錄裡找相關段落並附上引用來源。

![展示影片](docs/images/demo.gif)

五十秒的操作實錄：可用庫存與帳上庫存的差別、多階 BOM 展開的可製造量、
兩種來源的工單風險、MRP 的建議採購量、時間分期算出「第幾週開始缺」、
規則式與 ML 模型並陳的延遲風險，最後是測試綠燈。
**畫面上每一個數字都是真的打 API 跑出來的** —— 指令與輸出成對存在
[`tools/demo-video/data/`](tools/demo-video/data/)，可以自己重跑對照
（[原始 mp4](docs/videos/demo.mp4)、[產生方式](tools/demo-video/README.md)）。

![Scalar API 文件](docs/images/scalar-overview.png)

不想打 curl 的話，啟動後開 http://localhost:5199/scalar/v1 就是上面這個介面 ——
十六個端點都有中文說明與參數型別，可以直接在瀏覽器裡試打。

397 個測試通過、0 警告（本機裝了 Ollama 之後檢索品質那組會真的跑，共 427；
沒裝時它們標記為 6 個 skip，另有 3 個接真實 Anthropic API 的測試同樣 skip）。

**還沒對真的模型發過請求**：`AnthropicLlmClient` 送出的 HTTP 請求內容已用本機
假伺服器逐欄檢查，整條路徑也實跑通過一次 —— `/api/ai-assistant/ask` → tool-use 迴圈
→ OmniRoute gateway → provider → 工具查到真實的 seed 資料 → 最終答案，
九個工具逐一驗、平行呼叫與錯誤路徑都走過。**但 provider 那端是本機 stub，不是真的模型**，
而且沒打過 `api.anthropic.com`。接正式端點的測試（`AnthropicLiveApiTests`）已經寫好，
設好金鑰跑一次就會產出可歸檔的紀錄 —— 見「真實 API 驗證怎麼跑」。
RAG 那一側已對真實 Ollama（`bge-m3`）實跑驗證過 —— 而且那一輪實測換掉了預設模型，
見「為什麼不是 nomic-embed-text」。

### 想自己點點看

一份 `Dockerfile` 就能把展示站跑起來（Render / Fly.io 的設定檔在
[`deploy/`](deploy/)，含逐步說明）：

```bash
docker build -t erp-demo . && docker run --rm -p 8088:8080 erp-demo
```

然後開 <http://localhost:8088/>（導覽）或 <http://localhost:8088/scalar/v1>（互動文件）。
展示資料每次啟動重建，隨便打不會弄壞任何東西。

同一份 Dockerfile 也能直接部署到 Render 或 Fly.io（設定檔與逐步說明都在
[`deploy/`](deploy/)）。**公開展示站上的 AI 助理端點會回 503，那是刻意的**：
把 Anthropic 金鑰放在公開網址上，等於每一次請求都花錢而且擋不住任何人 ——
要開放的話至少得先有每 IP 速率限制、每日花費上限與隨時可關的開關。
其餘端點（庫存、BOM、工單風險、MRP、時間分期、ML 延遲風險）都不依賴外部服務，
在展示站上完全可用。

- **趕時間的話看這份** —— [`docs/one-pager.md`](docs/one-pager.md)，一頁式重點
- **三十秒跑起來、實際輸出、設計問答** —— [`docs/demo-and-design-notes.md`](docs/demo-and-design-notes.md)
- **架構決策與踩過的坑** —— 本文件以下各節
- **工具契約、庫存計算基準、防幻覺機制** —— [`docs/ai-assistant-module-plan-v2.md`](docs/ai-assistant-module-plan-v2.md)（程式碼有五處註解指向它）
- **工單延遲風險預測（ML）的每個決策與取捨** —— [`docs/ml-risk-prediction-module-plan-v1.md`](docs/ml-risk-prediction-module-plan-v1.md)
- **RAG 的範疇、資料流、七個決策點** —— [`docs/rag-module-plan-v1.md`](docs/rag-module-plan-v1.md)
- **規劃與實作的逐條對帳、剩餘工作** —— [`docs/ai-assistant-module-plan-v3.md`](docs/ai-assistant-module-plan-v3.md)
- **最初的規劃長什麼樣** —— [`docs/ai-assistant-module-plan-v1.md`](docs/ai-assistant-module-plan-v1.md)（動工前原貌）

規劃演進是 **v1 原始構想 → v2 動工前修訂 → v3 實作完成後對帳**。

## 專案結構

```
src/
  Erp.Domain          實體與領域規則，不依賴任何外部套件
  Erp.Application     使用案例服務 + Repository 介面與 IAiAssistantService（port）
  Erp.Infrastructure  Persistence（EF Core）、AI（Anthropic tool-use）、
                      Rag（Ollama embedding + 向量檢索）三個平行子系統
  Erp.Api             HTTP 端點
tests/
  Erp.Application.Tests      Application 層單元測試（以 in-memory 假 Repository 驅動）
  Erp.Infrastructure.Tests   EF Core 整合、tool-use 迴圈、wire format、RAG、ML 推論
  Erp.Api.Tests              HTTP 端點測試（例外 → 狀態碼對映）
  Erp.ArchitectureTests      分層邊界測試（Domain 不得碰 AI 或 EF Core）
docs/
  demo-and-design-notes.md         展示腳本與設計問答
  ai-assistant-module-plan-v3.md   現行規劃：實作對帳與剩餘工作
  ai-assistant-module-plan-v2.md   設計規範：工具契約、庫存基準、防幻覺機制
  ai-assistant-module-plan-v1.md   最初的規劃（動工前，保持原貌）
  rag-module-plan-v1.md            文件語意檢索的規劃與七個決策點
```

Domain 完全不知道 AI 的存在：`IAiAssistantService` 定義在 Application，實作在 `Infrastructure/AI`，
與 `Infrastructure/Persistence` 平行，跟既有的 Repository 一樣是依賴反轉。

`Infrastructure/Rag` 是第三個平行子系統，依賴方向是 `AI → Rag → Persistence`。
Domain 與 Application 都不知道向量或 embedding 的存在，由架構測試保護。

依賴方向固定為 `Api → Infrastructure → Application → Domain`，Domain 不知道上層存在。

## 開發環境

需要 .NET 10 SDK。本機以 Homebrew 安裝時要設定：

```bash
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"
```

建置與測試：

```bash
dotnet build && dotnet test
```

格式由 `.editorconfig` 規範（C# 4 空格、專案檔與 JSON/YAML 2 空格），CI 會驗證：

```bash
dotnet format --verify-no-changes
```

啟動 API（開發模式會自動建表並灌入展示資料）：

```bash
dotnet run --project src/Erp.Api --urls http://localhost:5199
```

啟動後開 **http://localhost:5199/scalar/v1** 是互動式 API 文件（Scalar）：
十六個端點都有中文說明與參數型別，可以直接在瀏覽器裡試打，不必寫 curl。
只在開發環境開放 —— 正式環境不需要把端點結構公開出去。

資料庫是 SQLite 檔（`src/Erp.Api/erp.db`），刪掉再啟動就會重新產生一份乾淨的展示資料。

## 目前進度

| 階段 | 狀態 |
|---|---|
| Phase 1 — AI 工具背後的查詢／計算服務 | 完成，含 EF Core 資料層與種子資料 |
| Phase 2 — Infrastructure.AI 與 tool-use 迴圈 | 完成；**尚未對真實 API 驗證過**（見下方） |
| Phase 3 — 補完 8 個工具、架構測試與防幻覺測試 | 完成 |
| Phase 3.5 — 規劃對帳後補齊的缺口（錯誤處理、錯誤碼、稽核 log、user-secrets） | 完成 |
| Phase 4 — 展示準備 | 完成（`docs/demo-and-design-notes.md`）；真實 API 驗證的測試已就緒，待金鑰實跑 |
| Phase 5 — 文件語意檢索（RAG） | 完成，已對真實 Ollama 實跑驗證（實測換掉了預設 embedding 模型） |
| Phase 6 — 把已知限制逐條收掉 | 完成（對話記憶、風險視窗、MRP 重複計算、時間分期、檢索評測集、角色隔離） |
| Phase 7 — 工單延遲風險預測（ML） | 完成（[`docs/ml-risk-prediction-module-plan-v1.md`](docs/ml-risk-prediction-module-plan-v1.md)），訓練資料是模擬的 |
| Phase 8 — Agent 能力擴充 | 完成（prompt injection 對抗測試、可寫入工具 + 人工確認流程） |
| Phase 9 — 展示與部署 | 完成（展示影片、一頁式摘要、容器化與 Render／Fly.io 設定）；公開網址待部署 |

### 十個 Application 服務（AI 工具背後真正做事的地方）

- `ItemMasterQueryService` —— 料號／品名關鍵字搜尋
- `InventoryQueryService` —— 帳上／保留／可用庫存查詢
- `BomExplosionService` —— 多階 BOM 展開、以可用庫存試算最大可製造量與缺料件
- `WorkOrderProgressService` —— 工單途程進度查詢
- `WorkOrderRiskService` —— 工單延遲風險判定（逾期 + 缺料兩種來源）
- `MrpCalculationService` —— MRP 缺料試算與建議採購量
- `PurchasingQueryService` —— 未結案採購單查詢
- `QualityInspectionQueryService` —— 品管檢驗結果彙總
- `PurchaseSuggestionService` —— 採購建議：AI 寫建議、人工核准才成立採購單
- `WorkOrderDelayRiskPredictionService` —— 延遲風險：規則式與 ML 模型並陳

## 四個必須知道的計算約定

這四條寫死在程式碼與測試裡，改動前先看 [`docs/ai-assistant-module-plan-v3.md`](docs/ai-assistant-module-plan-v3.md)：

1. **可行性計算一律以 `AvailableQty`（帳上 − 已保留）為基準**，不使用帳上庫存。用帳上庫存會把別張工單保留的料重複計入，導致「系統說夠、現場缺料」。
2. **`RequiredPerFinishedUnit` 的分母是最終成品一個單位**，多階 BOM 的中間階用量會逐層累乘。例如 `TV-100 → CHASSIS-02 ×3 → SCREW-05 ×4`，螺絲對成品的用量是 12 而不是 4。
3. **BOM 展開一律展到葉節點原料，不動用半成品既有庫存。** 這會低估可製造量但不會高估，對交期判斷是安全方向。
4. **MRP 的毛需求只算「尚未全數發料」的未結案工單。** 料一旦發到現場就已經從帳上庫存扣掉了，再把那張工單的剩餘產量算成需求，等於同一份料被算兩次 —— 展示資料裡就有一張這樣的工單，曾讓面板淨缺從 120 片被灌水成 130 片。

## AI 助理

預設不直接打 Anthropic 官方，而是走本機的 [OmniRoute](https://github.com/diegosouzapw/OmniRoute) gateway。
`AnthropicLlmClient` 的轉換程式碼一行都不用改：OmniRoute 有 Anthropic 相容的
`/v1/messages` 端點，只要把 `AiAssistant:BaseUrl` 指到它的根網址，
`/v1/messages` 這段由 SDK 自己接上去。

```bash
npm install -g omniroute && omniroute   # 另開一個終端機跑著，預設 20128 埠
```

金鑰改填 OmniRoute 儀表板 → Endpoints 的那一把（格式是 `sk-<machineId>-<keyId>-<crc>`）。
本機開發用 user-secrets（存在專案外，不可能被誤 commit）：

```bash
dotnet user-secrets set "AiAssistant:ApiKey" "sk-..." --project src/Erp.Api
```

`./scripts/set-api-key.sh` 只收 `sk-ant-` 開頭的官方金鑰，擋掉貼錯東西的情況 ——
OmniRoute 的金鑰請用上面那行直接寫入。金鑰不要寫進 `appsettings.json`。

模型代號也是 OmniRoute 的，預設釘死 `anthropic/claude-opus-5`（帶 provider 前綴的完整代號），
所以 OmniRoute 那邊要先連好一個能出 Claude 的 provider。
九個工具全靠 tool-use，釘死才知道是誰在答、答得好不好；
換成 `auto` 是交給它按當下可用的 provider 自動選（帶 `tools` 的請求會走它的
tool-bearing bypass，轉給單一模型處理，tool-use 不會被拆散），但路由到哪個模型是 runtime 才知道的事。

跟官方端點有一個行為差異是實測出來、程式碼有處理的：上游 provider 回空內容時，
OmniRoute 會補一個寫死 `(empty response)` 的 text 區塊（官方端點只會回 `tool_use`，
不補這一塊）。`AnthropicLlmClient` 會把這個佔位符丟掉，`AiAssistantService` 則在
最終回應完全沒有文字時回一句說得清楚的話，不會把英文佔位符或空字串當成答案送出去。

啟動時會印出生效的設定、端點與金鑰來源（只印來源、不印值）：

```
AI 助理設定：模型 anthropic/claude-opus-5，端點 http://localhost:20128（server-side refusal fallback 關閉），工具迴圈上限 5 輪，逾時 60 秒，API 金鑰來源：設定檔或 user-secrets
```

### 改回直接打 Anthropic 官方

`appsettings.json` 的 `AiAssistant` 拿掉 `BaseUrl`、`UseServerSideFallback` 設回 `true`、
`Model` 改回 `claude-opus-5`，金鑰換成 `sk-ant-...`（或環境變數 `ANTHROPIC_API_KEY`）即可。

`UseServerSideFallback` 管的是 Anthropic 的 server-side refusal fallback ——
安全分類器拒答時自動改由 `claude-opus-4-8` 作答。那是官方端點才有的請求參數，
所以打 gateway 時 beta 旗標與 `fallbacks` 整組不送（不是送成 `null`，是整個欄位不出現），
備援交給 OmniRoute 自己的路由做。兩層備援疊在一起只會讓「這句話是誰答的」變得無法追。

```bash
dotnet run --project src/Erp.Api --urls http://localhost:5199
```

```bash
curl -X POST http://localhost:5199/api/ai-assistant/ask \
  -H 'Content-Type: application/json' \
  -d '{"question":"面板還有多少可以用？"}'
```

沒設金鑰時回 503 與清楚訊息，不會洩漏 SDK 堆疊。

### 可寫入工具與人工確認

`suggest_purchase_order` 是唯一會寫入資料的工具，而它寫出來的是一筆
**狀態為「待人工確認」的採購建議**，不是採購單：

```bash
# AI 端：產生建議（狀態 PendingApproval，沒有任何採購單成立）
curl -X POST http://localhost:5199/api/ai-assistant/ask \
  -H 'Content-Type: application/json' \
  -d '{"question":"缺料的部分幫我開採購建議"}'

# 人工端：看清單、核准或駁回。核准才會產生正式採購單
curl "http://localhost:5199/api/purchase-suggestions?status=PendingApproval"
curl -X POST http://localhost:5199/api/purchase-suggestions/PS-20260913-001/approve \
  -H 'Content-Type: application/json' -d '{"decidedBy":"王採購"}'
```

**為什麼即使 LLM 已經只能呼叫工具，寫入還要多一層人工確認**：理由不是籠統的
「怕 LLM 出錯」。採購會產生對外的金錢承諾，而 LLM 的輸入 —— 使用者的一句話、
檢索到的文件內容 —— 都是它控制不了的。把「產生建議」與「成立承諾」分開之後，
最壞的結果就只是多一筆要被駁回的建議。這是目前 agentic 系統設計的業界共識模式。

分界線落在程式碼的哪裡：

- `SuggestFromShortagesAsync` 是 AI 工具唯一通得到的入口，它只寫得出 `PendingApproval`。
- `ApproveAsync` 是整個系統唯一會新增採購單的地方，只有那三個 HTTP 端點呼叫得到，
  **工具目錄裡沒有任何東西通得到它**（有一條測試釘住 `approve_purchase_suggestion`
  這類名稱不會出現在目錄裡）。

三個實作上的決定：

- **重複的建議會被跳過。** LLM 重複呼叫同一個工具很常見（它看不到上一次呼叫的副作用），
  同一個料號還有待確認的建議時就不再產生，回傳的 `skipped_item_codes` 會說明。
  已被駁回的不算 —— 駁回代表「這次不買」，不是「以後都不要再提」。
- **已處理過的建議不能重複核准**（回 409）。重複核准會變成兩張採購單。
- **採購單的預計到貨日是「今天 + 採購前置期」，不是建議裡的需求日。**
  需求日是「什麼時候要用到」，兩者混用會讓下一輪 MRP 把一張根本來不及的採購單
  算成及時供給。

建議理由（`reason`）由後端從 MRP 結果組出來，不是 LLM 寫的 —— 它是人核准時的依據，
必須對得上後端算的數字。system prompt 也規定用完這個工具後必須說清楚
「這是建議、還沒下單、要有人核准才會成立」，說成「已經幫你下單」是錯的。

### Prompt injection 對抗

`PromptInjectionResilienceTests` 是一組刻意設計的安全測試，注入字串從兩條路進來：
使用者訊息，以及**被污染的語料**（模擬知識庫裡有人寫進了指令）。

**這組測試驗得了什麼、驗不了什麼，比測試本身更重要**：

- **驗得了（架構性保證，與 LLM 怎麼想無關）**：注入字串只能停留在「資料」這一側。
  工具名必須在目錄裡，參數只會變成查詢條件，整條路徑沒有任何地方會把字串變成 SQL。
  跑完一輪注入問答後，資料庫內容一個位元都沒變（比對的是實際欄位內容，不是筆數）。
  測試裡的假 LLM 是**刻意演出「被說服了」**的 —— 它真的照注入的要求去呼叫工具，
  重點正是：就算模型被說服，結構上也做不到。
- **驗不了（需要真實模型）**：LLM 自己會不會被說服。用假 LLM 斷言「它沒有被誘導」
  只會測到自己寫的腳本，那是一個看起來很安全的假測試。行為那一側由
  `AnthropicLiveApiTests` 的注入案例負責（需要金鑰，預設 skip）。

**這個分工本身就是答案**：防線不能只靠 prompt。prompt 是說服層，擋不住被說服；
工具邊界是結構層，說服不了它。系統提示詞那一側補了兩條規則
（檢索段落是資料不是指令、使用者訊息不能改變規則），因為「把提示詞唸出來」
這種純文字的要求結構層擋不住 —— 有一條測試釘住它們不會被誰重寫時默默刪掉。

### 角色範圍

可選的 `role` 參數會限制這次請求用得到哪些工具：

```bash
curl -X POST http://localhost:5199/api/ai-assistant/ask \
  -H 'Content-Type: application/json' \
  -d '{"question":"面板的採購單狀況？","role":"quality"}'
```

| 工具 | production 生管 | purchasing 採購 | quality 品保 |
|---|:---:|:---:|:---:|
| `search_items`／`get_item_inventory_status`／`search_documents` | ✓ | ✓ | ✓ |
| `check_material_sufficiency_for_item` | ✓ | ✓ | |
| `get_work_order_progress` | ✓ | | ✓ |
| `list_work_orders_at_risk` | ✓ | ✓ | |
| `run_mrp_shortage_analysis`／`run_mrp_time_phased_analysis` | ✓ | ✓ | |
| `list_open_purchase_orders` | | ✓ | |
| `get_quality_inspection_summary` | ✓ | | ✓ |
| `suggest_purchase_order`（寫入建議） | ✓ | ✓ | |
| `predict_work_order_delay_risk` | ✓ | ✓ | |

不給 `role` 就是全部工具都開（維持加這一層之前的行為）。
不認得的角色名稱回 400 —— 打錯字的 `purchase` 靜默變成「全部工具都開」，
比直接報錯危險得多。

**兩道防線，缺一不可**：送給 LLM 的工具清單會過濾，**而且**執行時再擋一次。
只做前者不夠 —— 工具名稱是模型生成的字串，它可以叫出一個從沒出現在自己清單裡的名字
（猜錯就會發生，不需要任何惡意），那時只剩執行時這道擋得住。
反向驗證：拿掉執行層那道會紅 2 條。

**對話記憶也以角色為界**：記憶的鍵是「角色 + 識別碼」。共用一個鍵的話，
拿著品保的識別碼改用採購角色再問一次，就讀得到品保那一段歷史 ——
工具過濾擋住的東西會從歷史繞回來。反向驗證：把鍵改回只有識別碼會紅 1 條。

**這一層是什麼、不是什麼**：它是工具層級的邊界，不是資料列層級的隔離
（允許的工具仍然查得到全庫資料），也不是認證（`role` 由呼叫端自己填，
這個系統沒有登入）。它擋得住一件具體的事：agentic 系統把全部能力
一視同仁地攤開給每個呼叫者 —— 品保問得到供應商的交易條件，
不需要任何越權技巧，只要問一句就行。

### 對話記憶

回應會帶一個 `conversationId`，下次請求帶著它就能接續同一段對話：

```bash
curl -X POST http://localhost:5199/api/ai-assistant/ask \
  -H 'Content-Type: application/json' \
  -d '{"question":"那 CABLE-07 呢？","conversationId":"<上一次回應裡的識別碼>"}'
```

三個決定值得說明：

- **識別碼由伺服器產生，不接受呼叫端自己挑。** 放行任意字串的話，
  猜一個別人用過的 id 就能讀到別人的對話歷史。不是 GUID 一律回 400。
- **歷史只存問答文字，不存工具呼叫與工具結果。** tool_use 與 tool_result
  必須成對且緊鄰，歷史裡塞半套會變成 API 格式錯誤；而且同一段 JSON 每輪重送是純粹的
  token 浪費。代價是 LLM 看不到上一輪的完整工具輸出，追問細節時它會再查一次 ——
  這反而保證數字是當下查的，不是從歷史裡抄的。
- **存在記憶體，不落地。** 對話上下文是短暫的，做成資料表就得回答「誰來清、保留多久、
  要不要備份」這一整串與這個模組無關的問題。代價是重啟後歷史消失。
  保留最近 6 輪、最多 200 個對話（滿了淘汰最久沒被碰過的）、閒置 60 分鐘丟棄 ——
  兩個上限都是必要的：這是長時間執行的服務，沒有上限的話每個新對話都會永久佔著記憶體。

### 真實 API 驗證怎麼跑

`AnthropicWireFormatTests` 是用本機假伺服器驗的，它照單全收 ——
工具的 input schema 不合法、beta 標頭被拒、fallback 參數改名，
測試都會綠，而正式環境第一次呼叫就 400。`AnthropicLiveApiTests` 補的就是這個缺口：
接**正式端點**跑完整條 tool-use 迴圈。設好金鑰後：

```bash
dotnet test tests/Erp.Infrastructure.Tests --filter "FullyQualifiedName~AnthropicLive"
```

沒金鑰時整組 skip（所以預設不會讓任何人的 `dotnet test` 紅掉、也不會花到錢）。
金鑰沿用上面 user-secrets 的設定，或讀 `ANTHROPIC_API_KEY`。

比照 RAG 那邊的做法，有一個 `ANTHROPIC_REQUIRE_LIVE=1` 開關讓它**不准 skip** ——
沒有它的話，金鑰忘了設會讓整組安靜變成 skip 而回合照樣綠，那是一條假防線。
實測：不設金鑰並設這個變數，兩條都以 401 紅掉、0 skip。

斷言刻意寬鬆（真模型的措辭每次都不同，釘字面只會製造脆弱的測試），只釘三件會真的壞掉的事：
請求有沒有被接受、該用的工具有沒有被選到、回答裡的數字是不是工具回傳的那一個。
通過時會把逐輪對話寫成 `docs/verification/` 底下的純文字紀錄（金鑰已遮蔽），
讓這次驗證可以被歸檔，而不是跑完就散在 console 裡。

### 十二個工具

十一個唯讀查詢加一個寫入。寫入的那個（`suggest_purchase_order`）寫出來的是
**待人工確認的建議**，不是採購單 —— 見「可寫入工具與人工確認」。
除了 `search_documents` 之外都只是薄薄一層，把參數轉交給既有的 Application Service，
數字一律由後端算好。

| 工具 | 對應服務 |
|---|---|
| `search_items` | `ItemMasterQueryService` |
| `get_item_inventory_status` | `InventoryQueryService` |
| `check_material_sufficiency_for_item` | `BomExplosionService` |
| `get_work_order_progress` | `WorkOrderProgressService` |
| `list_work_orders_at_risk` | `WorkOrderRiskService` |
| `run_mrp_shortage_analysis` | `MrpCalculationService` |
| `run_mrp_time_phased_analysis` | `MrpCalculationService`（時間分期：什麼時候會缺，不是缺多少） |
| `list_open_purchase_orders` | `PurchasingQueryService` |
| `get_quality_inspection_summary` | `QualityInspectionQueryService` |
| `search_documents` | `DocumentSearchService`（`Infrastructure/Rag`，不經 Application） |
| `suggest_purchase_order` | `PurchaseSuggestionService`（**唯一會寫入**，只寫得出待人工確認的建議） |
| `predict_work_order_delay_risk` | `WorkOrderDelayRiskPredictionService`（規則式與 ML 模型並陳） |

`search_documents` 是唯一不轉呼叫 Application Service 的工具：語意檢索是基礎設施能力
（embedding HTTP 呼叫與向量運算），不是領域使用案例。讓它經過 Application 就得在那裡
定義一個帶相似度分數的型別，而「Domain／Application 不得知道向量這回事」是架構測試釘住的規則。
取捨見 [`docs/rag-module-plan-v1.md`](docs/rag-module-plan-v1.md) 決策 D5。

新增工具要改三個地方，少改一個測試就會紅：`ToolCatalog.All`、`ToolDispatcher`
的 switch、以及 `ToolCatalogConsistencyTests.SampleValue`（沒有範例值會直接擲錯）。

### 組成

| 類別 | 職責 |
|---|---|
| `ILlmClient` + `Llm*` 中性模型 | 供應商抽象。換一家 LLM 只要換 `AnthropicLlmClient`，迴圈與工具定義都不動 |
| `AnthropicLlmClient` | 官方 Anthropic C# SDK 的型別轉換；把 SDK 例外轉成 `LlmUnavailableException` |
| `ToolCatalog` | 工具的名稱、說明與 JSON Schema，集中一份 |
| `ToolDispatcher` | 工具名稱 → 呼叫既有 Application Service，序列化成 snake_case JSON |
| `AiAssistantService` | tool-use 迴圈，輪數上限預設 5 |
| `UnicodeNormalizingHandler` | 見下方「中文逃逸」 |
| `NormalizedDecimalConverter` | 數量輸出 `30` 而不是 `30.0`，見下方 |

### 工具錯誤契約

工具失敗時回傳 `{ "error_code": ..., "message": ... }`，`message` 給人看，
`error_code` 讓 LLM 有依據決定下一步（system prompt 逐碼說明該怎麼反應）：

| error_code | 情境 | LLM 該做的事 |
|---|---|---|
| `ENTITY_NOT_FOUND` | 資料不存在 | 告知查不到，不要重試 |
| `INVALID_ARGUMENT` | 參數缺漏或格式不對 | 依 message 修正後可重試一次 |
| `NOT_APPLICABLE` | 參數合法但問法不適用（如對原物料問可製造量） | 說明原因，不要重試 |
| `SERVICE_UNAVAILABLE` | 依賴的外部服務沒跑（RAG 需要的 Ollama） | 說明原因並建議改用結構化查詢，不要重試 |
| `NOT_AUTHORIZED` | 工具存在，但這個角色沒有權限 | 如實說明不在查詢範圍內，不要重試、也不要繞過 |
| `UNKNOWN_TOOL` / `INTERNAL_ERROR` | 呼叫了不存在的工具／未預期錯誤 | 不要重試 |

未預期例外一律降級成單一工具的失敗，不會讓整段對話回 500；
例外全文只進伺服器 log，回給 LLM 的內容不含型別、堆疊或內部細節。

### 稽核軌跡

每次工具呼叫都留一筆結構化紀錄 —— 助理最後只吐出一段自然語言，
沒有這條 log 就無從得知那段話是根據哪些查詢組出來的：

```
工具呼叫 get_item_inventory_status 完成，成功：True，耗時 12 ms，參數：{"item_code":"PANEL-01"}
```

未預期例外另外記一筆 `Error`，含例外全文；回給 LLM 的內容則不含任何內部細節。

RAG 的檢索層另外記一筆，因為通用那行記不到它獨有的兩個數字：

```
文件檢索完成，片段總數 33，命中 2 段，最高相似度 0.7673，門檻 0.5，耗時 80 ms
```

**最高相似度是過濾之前的**，而且刻意不回給 LLM。助理說「文件裡查不到」之後，
要判斷是語料真的沒有、還是門檻調太高，只有這個數字能回答。

## 文件語意檢索（RAG）—— 可選模組

**沒裝 Ollama 也能跑。** 核心 ERP 與其餘十一個工具完全不依賴它，只有 `search_documents`
會回 `SERVICE_UNAVAILABLE` 並說明原因。`dotnet run` 直接跑得起來，不會因為少裝東西而啟動失敗。

要啟用的話需要本機 Ollama：

```bash
brew install ollama && ollama serve
```

```bash
ollama pull bge-m3
```

然後刪掉 `src/Erp.Api/erp.db` 重新啟動，索引會在 seeding 之後自動建立。
啟動時會印出索引狀態（跟金鑰來源那行同一個理由 —— 否則只能靠猜）。兩條都是實跑貼上的：

```
文件檢索索引建立完成：7 份文件、33 段、模型 bge-m3
文件語意檢索：索引 33 段，模型 bge-m3，相似度門檻 0.5
```

沒裝 Ollama 時印的是：

```
文件語意檢索：索引 0 段，模型 bge-m3，相似度門檻 0.5（索引未建立：需要本機 Ollama 並執行 ollama pull bge-m3；其餘工具不受影響）
```

### 技術選擇

- **Embedding**：本機 Ollama HTTP（`/api/embeddings`），模型 `bge-m3` ——
  零成本、離線可跑、不需要金鑰或雲端帳號。
- **向量儲存**：既有 SQLite 的 `document_chunks`，float32 BLOB ——
  不必多跑一個服務；1024 維一段 4096 bytes，33 段共約 132 KB。
- **相似度**：C# 手寫 brute-force cosine —— 33 段 × 1024 維約三萬四千次乘加。
  實測單次查詢 19–80 ms，其中絕大部分是 embedding 的那一次往返。

沒有引入 Qdrant／pgvector，也沒有引入 SIMD 套件。依賴增加了 —— 零個。

實測數字（`bge-m3`，33 段語料）：建索引 1969 ms（33 次 embedding 往返），
查詢 19–80 ms，索引 132 KB。

### 為什麼不是 nomic-embed-text

第一版預設用 `nomic-embed-text`，實跑之後換掉了。這是整個模組最值得記錄的一次實測：

| 模型 | 7 題相關查詢的 top-1 命中 | 相關查詢最高分 | **無關查詢最高分** | 有可用門檻嗎 |
|---|---|---|---|---|
| `nomic-embed-text`（768 維） | 1/7 | 0.648–0.759 | **0.594、0.622** | **沒有** —— 兩個區間重疊 |
| `bge-m3`（1024 維） | 4/7（top-3 含正確來源 7/7） | 0.579–0.757 | **0.401、0.456** | 有，0.5 落在中間 |

無關查詢是「今天天氣如何？」與「請幫我寫一段 Python 程式」。
在 `nomic-embed-text` 下它們拿到 0.59–0.62，比真正相關的查詢還高 ——
**這代表不存在任何門檻值能把相關與無關分開，防幻覺的第一道防線形同不存在**，
而單元測試完全看不出來（測試用的是假 embedding，驗得了「門檻有沒有被套用」，
驗不了「門檻值有沒有意義」）。

原因是語料與查詢都是中文，而 `nomic-embed-text` 是英文為主的模型。
也試過補上它要求的 `search_document:` / `search_query:` 任務前綴 —— 沒有改善（仍是 1/7）。

**門檻 0.5 因此不是一個通用常數，而是對「這個模型 + 這個語料」量出來的值。**
換模型後必須重新量。

### 語料與切段

展示語料是 7 份文件、33 個片段、3944 字，跟 TV-100 的結構化情境共用同一個故事世界：

| 文件 | 片段數 |
|---|---|
| 品管異常處理 SOP — 面板色偏／組裝異音／外觀尺寸超差 | 5 / 5 / 4 |
| 設備維修手冊摘要 — 面板貼合機／自動鎖螺絲機 | 5 / 4 |
| 客訴處理紀錄 — 面板色偏批量客訴／訊號線接觸不良 | 5 / 5 |

所以「WO 為什麼延遲」由結構化工具回答，「色偏該怎麼處理」由 `search_documents` 回答，
兩者在同一次對話裡互補而不重疊。

切段策略是**依段落切、不重疊**：`chunk_index` 與原文段落一對一，引用座標才精確。
太短的段落（標題行）會併進下一段 —— 否則它會變成一個幾乎沒有資訊的片段，白佔 `top_k` 的位置。

### 防幻覺：這裡的等價要求是「不能引用不存在的段落」

既有的原則是「所有數字都由後端算好，LLM 不能編造」。檢索的等價物是引用來源：

1. **相似度門檻判斷留在後端。** 低於門檻的片段根本不會出現在工具回傳值裡 ——
   讓 LLM 自己看分數決定「這段算不算相關」，等於把判準交給無法測試的一方。
2. **引用座標只能來自工具回傳值。** `source_name` 與 `chunk_index` 由後端給，
   system prompt 明文禁止改寫或推測文件名稱。測試斷言回傳的每一組座標都真的在資料表裡。
3. **查不到就是查不到。** `chunks` 為空時 system prompt 要求明確說「文件中查不到」，
   禁止改用模型自己的知識回答 —— 那會讓使用者以為那是公司文件的規定。
4. **`similarity` 不得當成百分比轉述。** 0.48 不是「48% 相關」。
5. **「索引沒建」與「查不到」是兩個不同的答案。** 前者回 `SERVICE_UNAVAILABLE`，
   後者回成功的空結果。混為一談會讓使用者以為文件裡真的沒寫。

### 檢索品質怎麼量

評測集是 `RetrievalEvaluationSet` 裡的 30 組手寫標註查詢，分五類：

| 類別 | 題數 | 這一類在驗什麼 |
|---|---|---|
| Direct | 13 | 用文件裡出現過的詞問，最基本的一組 |
| Paraphrase | 6 | 換句話說（「色偏」→「螢幕顏色不均勻」）。這是 embedding 該會、關鍵字比對做不到的事 |
| CrossDocument | 2 | 答案橫跨兩份文件，或兩份都沾得上邊 |
| OutOfScope | 4 | 主題沾得上邊、但語料裡根本沒寫（售價、付款條件、賠償金額） |
| Irrelevant | 5 | 與語料完全無關（天氣、匯率、勞健保） |

**2026-09-13 用 `bge-m3` 對這批語料實測的基線**：

| 指標 | 實測 | 測試設的門檻 |
|---|---|---|
| MRR@10 | 0.9206 | ≥ 0.85 |
| Recall@3 | 1.00（21/21） | ≥ 0.95 |
| Recall@1 | 0.857（18/21） | 不設門檻，只記錄 |
| 相關題最低分 / 無關題最高分 | 0.5788 / 0.4376 | 分離度 ≥ 0.1 |

門檻都設在實測值下方留一題的緩衝 —— 卡在實測值上，每次模型小版本更新都會紅。

**為什麼要 MRR 而不只是命中率**：正確答案從第 1 名掉到第 3 名，「前三段有沒有命中」
完全看不出來，MRR 會從 1.0 掉到 0.33。原本 8 組題目時少一題就掉 12.5 個百分點，
分數本身沒有解析度。

**量出來的一個限制**：OutOfScope 那四題拿到 0.55–0.63，落在真正相關題目的區間裡
（最低 0.5788）。相似度門檻分不開這兩者，把門檻拉高到擋得住它們，
就會同時擋掉真正相關的問題 —— 實測把門檻從 0.5 調到 0.7，Recall@3 直接掉到 0.38。
所以防線不在這一層，在 system prompt：「有檢索到東西不等於檢索到答案」，
那條規則就是量完這件事之後補上去的。

### 換模型要重建索引

`document_chunks` 每一列都記著 `embedding_model` 與 `dimension`。查詢時先比對模型名稱，
不一致就直接回錯誤並要求重建 —— 不同模型的向量空間不同，硬算會得到一個
**有數字但沒有意義**的相似度，而那種錯誤不會拋例外，只會讓排序靜靜地變成另一個樣子。

## 工單延遲風險預測（ML）—— 可選模組

規則式的 `WorkOrderRiskService` 回答「這張工單**為什麼**有風險」，
模型回答「它**多可能**延遲」。兩者並存，由
`predict_work_order_delay_risk` 這個工具同時回傳：

```bash
curl "http://localhost:5199/api/work-orders/WO-20260913-01/delay-risk"
```

```json
{"ruleBasedDelayDays":2,"ruleBasedReason":"缺料：PANEL-01 短少 120 件",
 "predictedDelayProbability":0.6856,"exceedsThreshold":true,
 "features":{"materialReadiness":0.4,"daysUntilDue":3,"maxLeadTimeDays":5,
   "progressRatio":0,"itemOverdueRate":0,"weeklyLoadRatio":0.125,"bomComponentCount":3}}
```

**訓練資料是模擬的，不是真實產線資料。** 生成規則是我自己寫的，
所以模型學得回那條規則幾乎是必然 —— 它證明的是 pipeline 接起來了、
每個決策講得清楚，不是這個模型對真實產線有效。完整的決策紀錄在
[`docs/ml-risk-prediction-module-plan-v1.md`](docs/ml-risk-prediction-module-plan-v1.md)，
下面只摘三件最值得講的。

### 一個因為「線上算不出來」而被換掉的特徵

第一版用了「該品項的歷史延遲率」，預測力更好、離線評估也更漂亮。
寫到線上推論才發現：**系統沒有記錄工單的實際完工日**，
「當初有沒有準時完工」這件事在資料裡根本不存在。

**特徵工程的第一個判準是「預測當下拿不拿得到」，不是「有沒有預測力」。**
一個離線算得出來、線上算不出來的特徵，離線評估會很漂亮，上線就是空的。
換成語意相近、現在就查得到的「該品項未結案工單中已逾交期的比例」。

### 閾值 0.26 是選出來的，不是預設的 0.5

| | precision | recall |
|---|---|---|
| 閾值 0.5（預設） | 0.6829 | 0.6043 |
| 閾值 0.26（採用） | 0.5605 | **0.8993** |

漏抓一張會延遲的工單，代價是客戶端的交期跳票；誤報的代價只是生管多看一眼。
兩者不對稱，閾值就不該用對稱的預設值。代價也講清楚：這個閾值下每抓到 125 張
真的會延遲的工單，會誤報 98 張 —— 接不接受是業務判斷，模型該做的是把取捨攤開，
不是替人選好。

ROC AUC 是 0.7386 而不是 0.99：生成規則裡有 5% 標籤翻轉，延遲與否又是伯努利抽樣，
本來就不是可以完全預測的。一個假到 0.99 的數字反而該懷疑。

### training/serving skew 的三道防線

同一個特徵在訓練與線上算得不一樣，是這類系統最典型也最難查的失敗：
不報錯、離線看不出來、線上算出來的機率卻是拿錯尺量的。

1. **特徵定義只有一份**（`WorkOrderDelayFeatures`），而且在 C# 這一側 ——
   訓練資料由它產生，線上推論也由它組出來。這是「資料生成寫在 C# 而不是
   訓練腳本裡」的理由：Python 只負責它真正擅長的事（訓練與評估）。
2. **黃金樣本**：訓練時把七組輸入與 sklearn 算出的機率存進 metadata，
   測試驗 ONNX 載進 .NET 後算出同一個數字。反向驗證：把 `ToVector` 前兩欄對調，
   **只有這一條會紅**；機率取到第 0 欄（不延遲），紅 2 條。
3. **端到端實跑**——而這一道真的抓到了一個 bug：`bom_component_count` 原本寫成
   「有缺料時用缺料件數、沒缺料時用葉節點數」，單元測試全綠、離線評估正常，
   打一次 API 才看出線上算出來是 1 而訓練資料裡是 2~8。同一個特徵兩種意思。

### 重新訓練

```bash
dotnet run --project tools/Erp.MlDataGen          # 產生模擬歷史資料
python3 -m venv ml/.venv && ml/.venv/bin/pip install -r ml/requirements.txt
ml/.venv/bin/python ml/train.py                    # 訓練、評估、匯出 ONNX
```

**推論不需要 Python。** 模型以 ONNX 進 repo，由 `Microsoft.ML.OnnxRuntime` 載入 ——
clone 下來跑 `dotnet test` 不必裝任何 Python 套件。模型檔載不起來時整個服務照常啟動，
預測會如實說「沒有模型」，而不是回一個看起來像機率的預設值。

## API 錯誤處理

`ErpExceptionHandler` 把 Application 層的例外對映成語意正確的狀態碼。
沒有這一層時，查無料號會回 500，而且回應體直接吐出完整堆疊與本機絕對路徑。

- `EntityNotFoundException` → **404**
- `ArgumentException`（含 `ArgumentOutOfRangeException`）→ **400**
- `InvalidOperationException` → **409**（參數合法但操作不適用，如對原物料問可製造量）
- `LlmUnavailableException` → **503**
- 其他 → **500**，回應只有通用訊息，全文進伺服器 log

## 實作時踩到的坑

依「發現時的代價」排序。每一條都是測試或實測抓到的，不是事後回想。

**灌種子資料不是併發安全的**（真 bug，flaky 測試追出來的）：API 測試出現「每個 build
組態的第一次執行才失敗」的怪症狀，錯誤是 `UNIQUE constraint failed: bom_lines...`。
根因是 `WebApplicationFactory` 會建立 host 不只一次，冷啟動時兩次 seeding 真正重疊，
雙方都通過了「是否已有資料」的檢查；熱身後第一次太快完成，第二次就只看到資料而跳過。
第一次寫的併發測試沒抓到，因為它用 in-memory SQLite —— 共用單一連線，寫入天然被序列化。
改用檔案 SQLite 才重現得出來。這不只是測試問題：多個 API 實例同時啟動時，生產環境是同一個競態。

**MRP 漏算逾期工單**（真 bug）：查詢起點原本用「今天」，把交期已過但還沒做完的工單
整批擋掉了 —— 那些工單仍然要料，而且是最急的需求。用假 Repository 測不出來，
因為 fake 跟真 Repository 有同樣的過濾邏輯；是端到端測試對不上數字才追出來的。

**REST 端點完全沒有例外處理**：查無料號、查無工單、對原物料問可製造量、規劃天數為 0，
全部回 500，而且回應體直接吐出完整堆疊與本機絕對路徑。語意也錯 —— 查不到資料是 404 不是 500。
諷刺的是 AI 路徑上做了兩輪錯誤處理，REST 端點卻裸奔。修法見「API 錯誤處理」。

**未預期例外會炸掉整段對話**：`ToolDispatcher` 原本只攔三類已知例外，資料庫連線失效
會穿過 tool-use 迴圈變成 HTTP 500，即使同一輪其他工具的結果是好的也一起陣亡。
現在降級成單一工具的失敗。

**架構測試擋不住 Domain 引入 LLM SDK**：測試寫好了，反向驗證卻是綠的，一度以為測試無效。
真正的原因有兩層 —— 第一次的實驗用 `nameof` 寫（編譯期常數，不留型別參考，等於空實驗），
改掉之後才發現 NetArchTest 本身也擋不住。細節見「架構邊界」。

**EF Core 翻不動計算屬性**：`WorkOrder.IsOpen` 是 C# 計算屬性，寫在 `Where` 裡
會直接擲例外。抽出 `WorkOrderStatuses.Open` 當單一定義，讓記憶體判斷與 SQL 查詢共用。

**請求訊息共用可變 List**：`AiAssistantService` 把同一個 `List` 的參考傳進 `LlmRequest`，
之後還會往裡面 Add —— 任何暫存請求的實作（重試、記錄、批次）事後讀到的都是被竄改的內容。
只有加了會回頭檢查歷史請求的測試才抓得到。

**SDK 把中文逃逸成 `\uXXXX`**：系統提示詞、工具說明、使用者問題都是中文，
實測請求體積變成 2.28 倍（2038 → 893 字元），而工具說明每一輪都重送。
SDK 沒有序列化設定點，用 `DelegatingHandler` 在送出前重新序列化。
同一個問題在 log 也踩了一次 —— log 是給人看的，`\u9762` 讀不出來查了什麼。

**數量帶著沒有意義的小數尾巴**：SQLite 的 round-trip 會保留小數位數，`30m` 存進去
再取出變成 `30.0`。LLM 可能照抄成「短少 120.0 件」，每個數字也多花 token。
`NormalizedDecimalConverter` 去掉無意義的尾隨零，AI 工具與 REST 端點共用。

**我把 SQLite 的限制寫錯了，差點做出一條保護不存在問題的測試**：文件與註解原本寫
「decimal 存成 TEXT，資料庫層無法正確比較或排序」，那是從 EF Core 常識推來的、沒實測就寫下。
決定維持 SQLite 後要為這條約束加保護測試，動手前先實測 —— 結果比較、排序、加總全部正確，
EF Core 的 SQLite provider 會註冊 `ef_compare()`、`ef_sum()` 與 `EF_DECIMAL` collation。
那條約束不存在。改成 `SqliteDecimalBehaviourTests` 釘住真實行為，詳見「SQLite 的 decimal」。

**逐筆查詢的 N+1**：MRP 與工單風險判定在迴圈裡逐個料號查採購單與補料條件。
用假 Repository 完全看不出來，接上真資料庫才成為問題。判準用「查詢次數如何隨料號數成長」
而不是絕對次數 —— 絕對次數會隨 BOM 結構改變，斷言它只會製造脆弱的測試。

**選了一個在中文上不可用的 embedding 模型，而單元測試全綠**（做 RAG 時最有價值的發現）：
第一版預設 `nomic-embed-text`，255 個測試全過。裝上真的 Ollama 一跑才發現：
7 題相關查詢只有 1 題的 top-1 是正確文件，而「今天天氣如何」這種無關查詢拿到 0.62，
**比真正相關的查詢還高** —— 門檻 0.5 形同不存在，防幻覺的第一道防線是假的。
原因是語料與查詢都是中文，而那是英文為主的模型（補上它要求的任務前綴也沒改善）。
換成 `bge-m3` 後無關查詢掉到 0.40–0.46，門檻才真的有意義。

**教訓不是「選錯模型」，是「測試驗的是機制，不是效果」**：用假 embedding 能驗
「門檻有沒有被套用」，驗不了「門檻值有沒有意義」。這種缺口只有真的跑一次才會現形，
而它原本被寫在 README 的「尚未處理」裡當成一條可以接受的已知限制。

**測試設定被 appsettings 蓋掉，而且完全沒有症狀**（做線上展示站時踩到）：
`ErpApiFactory` 用 `ConfigureAppConfiguration.AddInMemoryCollection` 指定測試用的
連線字串，但 `Program.cs` 在 `builder.Build()` 之前就把連線字串讀走了 ——
那時工廠的設定還沒注入。結果是所有測試類別共用 `bin/` 底下那個相對路徑的 `erp.db`，
連跨天殘留的舊種子資料都一起繼承（實際被咬到的樣子：2026-09-14 跑的測試讀到 09-10 灌的工單）。
工廠註解宣稱的「每個測試類別一個獨立 SQLite 檔」那時已經很久沒有成立過，
**而且沒有任何一條測試會紅**。改用 `UseSetting`（寫 host configuration，優先級最高）。

這個坑還有第二層：補上的防線測試第一版讀的是 `IConfiguration`，
在壞掉的版本上照樣是綠的 —— 因為設定表裡確實有那個值，只是 `Program.cs` 讀得太早。
要驗的是 `DbContext.Database.GetConnectionString()`，也就是**真正連到哪個檔案**。
是反向驗證抓到測試驗錯了層級。

**一致性測試會被新工具反咬**（做 RAG 時踩到）：`ToolCatalogConsistencyTests` 會走訪每個工具
並斷言它不回錯誤，而 CI 上沒有 Ollama —— 檢索那個工具必然讓那兩條 Theory 變紅。
這讓 `IEmbeddingClient` 從「將來換供應商」的裝飾性抽象變成**必需品**：
測試要注入固定向量的假實作才能離線跑。抽象的真正理由常常不是原本宣稱的那個。

**完全相同的向量，cosine 不是精確的 1.0**（測試抓到的）：門檻原本在測試裡設成 `1.0`
來表達「只有完全相同的那一段會過關」，結果連它自己都被濾掉 ——
浮點累加出來是 `0.9999999999…`，而過濾用的是 `>=`。改成 `0.999`。
這類「邊界值剛好等於門檻」的假設在浮點數上永遠要留餘裕。

**假伺服器在用戶端逾時後連設定 `ContentLength64` 都會炸**（測逾時時抓到的）：
用戶端放棄後 `HttpListenerResponse` 已被釋放，那個賦值擲 `ObjectDisposedException`，
而它在 `try` 外面 —— 斷言其實通過了，測試卻在 `Dispose` 等待接聽迴圈時失敗。
症狀看起來像「逾時處理壞了」，實際上是測試替身自己的問題。

**工具參數不是單一 JSON 值**：`BetaToolUseBlockParam.Input` 的型別是屬性字典，
把 `JsonElement` 直接丟進去編不過，回送 tool_use 時要展開。

## 測試策略

397 個測試，分四個專案。檢索品質那組只在本機有 Ollama 時執行，
跑起來共 427 個；接真實 Anthropic API 的 3 個測試沒金鑰時 skip：

| 專案 | 數量 | 涵蓋 |
|---|---|---|
| `Erp.Application.Tests` | 93 | 計算邏輯（多階 BOM、風險判定、MRP、ML 特徵計算），用 in-memory 假 Repository |
| `Erp.Infrastructure.Tests` | 257（+30 需 Ollama，+3 需 Anthropic 金鑰） | EF Core 整合、tool-use 迴圈、錯誤契約、稽核 log、Anthropic 與 Ollama wire format、向量運算、切段、檢索與防幻覺；另有接真實模型的檢索品質測試 |
| `Erp.Api.Tests` | 35 | HTTP 端點的錯誤對映與正常路徑、展示環境行為、RAG 不可用時服務照常啟動（`WebApplicationFactory`） |
| `Erp.ArchitectureTests` | 12 | 分層邊界 |

**「30 個」與「6 個 skip」是同一組測試**：`OllamaTheory` 在 skip 時不展開
`InlineData`，所以沒裝 Ollama 的機器上看到的是 6 個 skip（2 個 Theory + 4 個 Fact），
裝了之後才會展開成 30 個實際執行的案例。兩個數字都對，只是計數的時機不同 ——
這件事值得寫出來，因為它看起來很像文件寫錯了。

### 端到端腳本（不在 CI 裡）

```bash
omniroute                      # 另一個終端機
./scripts/e2e-omniroute.sh
```

上面那些測試都停在 `AnthropicLlmClient` 的邊界：單元測試用假的 `ILlmClient`，
wire format 測試用假的 Anthropic 伺服器。**gateway 那一層的轉譯沒有任何測試看得到** ——
Anthropic 格式 ←→ OpenAI 格式、九個工具的定義、`tool_use`/`tool_result` 來回，
而那正是最容易默默壞掉的地方。這支腳本把整條打通一次，逐一驗九個工具、
平行工具呼叫與錯誤契約，全過回 0。

provider 端是 `scripts/fake-openai-provider.mjs`，照問句裡的 `@@CALL <工具> <參數>@@`
吐出指定的 tool_call —— 要驗的是路徑與轉譯，不是模型答得好不好，
所以 provider 必須可重複、不花錢。需要跑起真的 ERP 與 gateway，因此不放進 CI。

幾個值得一提的：

- **`ToolCatalogConsistencyTests`** — 工具的 JSON Schema 是手寫的，`ToolDispatcher` 用字串
  比對參數名，兩邊漂掉時 C# 編譯不會失敗。這組測試走訪目錄裡每個工具，
  **連選填參數都真的帶進去執行一次**（只測必填的話，選填參數改名不會被發現）。
- **`AnthropicWireFormatTests`** — 用本機假伺服器接住 SDK 真正送出的 HTTP 請求，
  逐欄檢查 body。不需要金鑰、不花錢。型別轉換編譯得過不代表 wire format 正確。
- **`AnthropicLiveApiTests`** — 唯一接**真實 Anthropic API** 的一組（沒金鑰時整組 skip）。
  假伺服器驗得了「我們送出的 JSON 長什麼樣」，驗不了「真實端點收不收」，這組補的是後者。
  同樣有 `ANTHROPIC_REQUIRE_LIVE` 這個不准 skip 的開關，理由跟 `RAG_REQUIRE_OLLAMA` 一樣。
- **`QueryEfficiencyTests`** — 斷言查詢次數如何「隨缺料料號數成長」，而不是絕對次數
  （那會隨 BOM 結構改變，只會製造脆弱的測試）。把 N+1 改回去會紅。
- **`VectorMathTests`** — cosine 的邊界案例：零向量回 0 不回 `NaN`、維度不一致擲例外
  而不是靜默比較前 N 維、等比例放大的向量相似度仍是 1（這條是「有沒有真的除以模長」的
  唯一證據）。反向驗證：拿掉正規化會紅 10 條。
- **`DocumentSearchServiceTests`** — 防幻覺那幾條。最有價值的一條是
  「查詢字串與某段落完全相同時該段排第一且引用座標對得上資料表」：它不依賴 embedding
  的語意品質（同一段文字必然得到同一個向量），驗的是 BLOB round-trip、評分、排序、
  引用座標整條鏈路。反向驗證：拿掉門檻過濾會紅 5 條。
- **`RetrievalQualityTests`** — 唯一接**真實 Ollama** 的一組（本機沒裝時整組 skip，
  CI 上會真的跑）。題目來自 30 組標註評測集，除了命中率還算 MRR 與 Recall@3
  當迴歸基準（見「檢索品質怎麼量」）。反向驗證：把門檻從 0.5 調到 0.7，
  MRR 掉到 0.357、Recall@3 掉到 0.381，這組紅 17 條。
  它存在的理由是一次真實事故：用假 embedding 的測試全綠，卻放過了一個在中文語料上
  不可用的模型。**最關鍵的一條是「與語料無關的問題必須回空結果」** ——
  門檻值有沒有意義，等價於「無關的問題會不會被擋下來」。
  反向驗證：把模型換回 `nomic-embed-text`，這組紅 5 條。
  另外有一個 `RAG_REQUIRE_OLLAMA` 開關（CI 會設）讓它**不准 skip** ——
  沒有這個開關，CI 上的 Ollama 安裝一旦悄悄失敗，整組會變成 skip 而 job 照樣綠，
  那是一條假防線。實測：停掉 Ollama 並設這個變數，9 條全紅、0 skip。
- **`OllamaEmbeddingClientTests`** — 用本機假伺服器檢查送出的請求，並逐一驗證
  連線被拒、404（模型沒 pull）、500、壞 JSON、沒有 `embedding` 欄位、逾時
  各自轉成什麼訊息。不需要裝 Ollama。
- **`ConversationMemoryTests`** — 驗的是「歷史有沒有被正確組進下一次請求」，
  **不是**「LLM 有沒有因此聽懂追問」。後者需要真實模型與行為評測，用假 LLM 去斷言
  它聽懂了，只會測到自己寫的腳本。另外釘住兩件容易忘的事：歷史裡不能出現
  tool_use／tool_result（成對且緊鄰是 API 的硬性要求），以及兩個對話不得互相污染。
  反向驗證：不把歷史組進訊息會紅 4 條，拿掉輪數截斷會紅 1 條。
- **`SystemPromptTests`** — 明確**不驗證 LLM 是否遵守規則**（那需要真實 API 與行為評測），
  防的是有人重寫 prompt 時把某條規則整個刪掉。

### 每條防線都做過反向驗證

把防線拔掉、確認測試會紅，再還原。沒有紅過的測試等於沒有測試。
二十九條防線的驗證結果列在 [`docs/demo-and-design-notes.md`](docs/demo-and-design-notes.md)。

這個習慣抓到過一次自己的錯誤：架構測試第一次反向驗證是綠的，一度以為測試無效，
深挖後發現是實驗寫錯 —— `nameof` 是編譯期常數不留型別參考，改用 `typeof` 就紅了。

## CI

兩個 job：

- **`build-and-test`** —— 格式檢查 → Release 建置（警告視為錯誤）→ 全部測試。
  維持快速回饋（實測約 1 分鐘）：它不需要 Ollama，檢索品質那組在這裡是 skip。
- **`retrieval-quality`** —— 裝 Ollama、pull `bge-m3`、只跑 `RetrievalQualityTests`。
  比主 job 慢得多（實測約 8 分鐘）。分開之後它紅燈的原因沒有模糊空間：
  要嘛模型選得不對、要嘛環境沒裝起來。

`retrieval-quality` 的兩個關鍵設計：

1. **不准 skip**：`RAG_REQUIRE_OLLAMA=1`。測試在 Ollama 不可用時會自動 skip，
   所以少了這個開關，安裝步驟只要悄悄失敗一次，整組就變成 skip 而 job 照樣綠 ——
   **假防線比沒有防線更糟**，因為它會讓人停止懷疑。
   實測：停掉 Ollama 並設這個變數，9 條全紅、0 skip。
2. **等服務真的起來**（輪詢 `/api/tags`）而不是盲等固定秒數，失敗時印出 `ollama serve` 的 log。

### 為什麼不快取那 1.2 GB 的模型

一開始加了 `actions/cache`，實測之後移掉 —— **它是負收益**：

| 步驟 | 無快取 | 有快取 |
|---|---|---|
| 還原／儲存快取 | 1 + 5 秒 | **8 + 0 秒** |
| 安裝 Ollama | 59 秒 | 76 秒 |
| **`ollama pull bge-m3`** | **5 秒** | 1 秒 |
| 執行測試（CPU 跑 embedding） | 133 秒 | 121 秒 |

**`pull` 只花 5 秒**（runner 在 Azure，1.2 GB ÷ 5 秒 ≈ 240 MB/s）。
快取省下 4 秒、花掉 13 秒。我原本假設「下載 1.2 GB 是瓶頸」，那是錯的 ——
時間花在安裝 Ollama 與 CPU 跑 embedding 上。

移掉快取連帶移掉了一整套只為它而存在的複雜度：原本要用 `OLLAMA_MODELS` 指定模型路徑，
因為 Linux 安裝腳本會建立 `ollama` 系統使用者、把模型放到
`/usr/share/ollama/.ollama/models`，快取 `~/.ollama/models` 會永遠是空的 ——
而那個錯誤的症狀只是「每次都重新下載」，不會有任何錯誤訊息。
（這個陷阱本身仍然成立，只是現在沒有快取需要它了。）

## 架構邊界

`Erp.ArchitectureTests` 讓分層規則被 CI 保護，而不是靠自律：

- Domain 不得相依於任何其他層，也不得參考 EF Core
- Application 不得相依於 Infrastructure（依賴反轉的方向）
- Domain 與 Application 都不得參考任何 LLM 廠商套件
- Domain 與 Application 都不得相依於 Rag 子系統，也不得參考任何向量或 embedding 套件
- Persistence、AI、Rag 是平行子系統：Persistence 不得相依於 AI 或 Rag，Rag 不得相依於 AI

「不得參考向量或 embedding 套件」那條是一份**預防性清單**（`System.Numerics.Tensors`、
`Microsoft.ML`、`Ollama*`、`Qdrant*` 等），目前沒有任何命中 —— RAG 刻意只用 `HttpClient`
與手寫 cosine。它防的是日後有人在 Application 裝一個向量套件，那時命名空間規則擋不住。
反向驗證的方式是臨時在 Application 加一個命中清單的 `PackageReference` 並實際用
`typeof` 引用它，確認測試會紅後還原 —— 實測紅了。

其中「不得參考 LLM 廠商套件」用的是組件參考檢查而不是命名空間規則 ——
NetArchTest 檢查的是 `Erp.*` 命名空間，對外部套件無感。實測在 Domain 裡寫
`typeof(Anthropic.AnthropicClient)` 時，只有組件參考那條會紅。
（注意 `nameof` 是編譯期常數，不會在 IL 留下型別參考，用它測不出違規。）

## 展示資料

`ErpDbSeeder` 灌入的情境所有日期與單號都以執行當天為基準相對產生，資料不會過期。
完整的展示腳本與每個數字的看點在 [`docs/demo-and-design-notes.md`](docs/demo-and-design-notes.md)。

產品結構：

```
TV-100  ├── PANEL-01   × 2
        ├── CHASSIS-02 × 3 ── SCREW-05 × 4   （螺絲對成品 = 12 支）
        └── CABLE-07   × 1
```

情境重點：面板帳上 100 片、保留 20 片，可用只有 80 片。

```bash
# 最多做 40 台（用帳上庫存會誤算成 50 台）
curl "http://localhost:5199/api/items/TV-100/sufficiency"

# 兩張風險工單，各延遲 2 天
curl "http://localhost:5199/api/work-orders/at-risk"

# 面板淨缺 120 片，建議下單 150 片
curl "http://localhost:5199/api/mrp/shortages"
```

這些數字都被 `SeededScenarioTests` 釘住，改動種子資料而沒同步更新文件時測試會先紅。

## 尚未處理

- **`/api/mrp/shortages` 仍然把需求日收斂到最早的那張工單**：若最急的是一張小需求，
  整批需求都會被貼上該日期，建議採購會偏保守。這是刻意保留的 ——
  「總共缺多少、要訂多少」與「什麼時候開始缺」是兩個問題，後者由
  `/api/mrp/time-phased` 回答（見「時間分期」）。兩個端點各司其職，
  把分期塞回前者只會讓一個回傳同時回答兩件事。
- **ML 延遲風險模型是用模擬資料訓練的**，而且展示資料的特徵落在訓練分布之外
  （`weekly_load_ratio` 算出來 0.125，訓練分布是 0.5~1.6）。沒有分布檢查、
  沒有機率校準、沒有模型監控與重訓機制。完整清單在
  [`docs/ml-risk-prediction-module-plan-v1.md`](docs/ml-risk-prediction-module-plan-v1.md)。
- **BOM 展開是逐階查詢**：每個節點一次資料庫往返，深層 BOM 會放大成本。
  正確解法是一次載入整棵樹或改用遞迴 CTE，目前資料量下不構成問題。
  （採購單與補料條件的 N+1 已消除，由 `QueryEfficiencyTests` 把關。）
- **`AnthropicLlmClient` 尚未對真的模型驗證過**。tool-use 迴圈由整組測試涵蓋，
  送出的 HTTP 請求內容也用本機假伺服器逐欄檢查過，整條路徑（端點 → 迴圈 → OmniRoute
  → provider → 工具查真實資料 → 最終答案）也實跑過一輪九個工具、平行呼叫與錯誤路徑，
  但 provider 那端是本機 stub，從未打過 `api.anthropic.com`（本機沒有金鑰）。
  接正式端點的測試（`AnthropicLiveApiTests`）已經寫好，設好金鑰跑一次就會產出
  可歸檔的紀錄 —— 見「真實 API 驗證怎麼跑」。
- **走 gateway 時 `tool_result` 的 `is_error` 旗標會被丟掉**：OpenAI 的訊息格式沒有這個欄位，
  OmniRoute 轉譯時只留下 content。這不影響錯誤處理 —— 系統提示詞是依 content 裡的
  `error_code` 決定怎麼做，不是依那個旗標，實測 `ENTITY_NOT_FOUND` 與
  `SERVICE_UNAVAILABLE` 都完整送到模型手上。打官方端點時旗標照常送出。
- **RAG 的檢索是 brute-force 全表掃描**：每次查詢載入全部向量。33 個片段無感
  （768 維 × 33 段約兩萬次乘加），數萬份文件要換 ANN 索引 —— 那時要換的是
  `DocumentSearchService` 一個類別，不是整個架構。
- **RAG 索引不會自動更新**：語料是編譯進程式的常數（`DemoCorpus`），改了要刪掉資料庫重建，
  與既有種子資料同一個模式。沒有文件上傳端點 —— 那會帶出權限、病毒掃描、檔案儲存
  一整串與本模組無關的問題。
- **RAG 沒有 reranking、沒有 query rewrite**：刻意不做。這些會讓「答案從哪來」變得難追，
  與「所有數字由後端算好」的方向相反。
- **相似度門檻（0.5）是對「`bge-m3` + 這個語料」量出來的值，不是通用常數**。
  換 embedding 模型後必須重新量 —— `nomic-embed-text` 下根本不存在可用的門檻值
  （見「為什麼不是 nomic-embed-text」）。
- **相似度門檻擋不住「主題沾得上邊、但語料裡根本沒寫」的問題**。問售價、付款條件、
  賠償金額，實測拿到 0.55–0.63 分，與真正相關題目的區間（最低 0.58）重疊 ——
  門檻拉高到擋得住它們，就會同時擋掉真正相關的問題。防線只能在 system prompt
  （「有檢索到東西不等於檢索到答案」），不在這一層。`RetrievalQualityTests` 有一條
  刻意釘住這個現況的測試，哪天有模型分得開它們，那條會紅並要求回頭更新這段敘述。
- **評測集是 30 組手寫標註**（21 有答案 + 4 邊界 + 5 無關），不是業界標準評測集。
  它有 MRR 與 Recall@3 兩個指標當迴歸基準（見「檢索品質怎麼量」），
  比原本的 8 組有解析度得多，但題目仍然是自己出的 —— 出題的人和寫語料的是同一個。
- **角色隔離只到工具層級，沒有資料列層級的隔離**：允許的工具仍然查得到全庫資料，
  沒有「只能看自己部門的工單」這件事。要做到那個量級，得在每個查詢服務裡下推
  呼叫者身分並過濾資料列。
- **角色不是認證**：`role` 是呼叫端自己填的，這個系統沒有身分驗證，
  填 `production` 和填別的沒有任何門檻。它擋得住「工具清單被一視同仁地攤開給每個人」，
  擋不住刻意越權的人。要補的是前面那一段（登入與身分），不是這一層。

## SQLite 的 decimal

`decimal` 欄位的型別是 TEXT，但**透過 EF Core 查詢時比較、排序、加總都是正確的** ——
SQLite provider 會在連線上註冊 `ef_compare()`、`ef_sum()` 與 `EF_DECIMAL` collation，
產生的 SQL 長這樣：

```sql
WHERE ef_compare("i"."OnHandQty", '50.0') > 0
ORDER BY "i"."OnHandQty" COLLATE EF_DECIMAL
```

`SqliteDecimalBehaviourTests` 把這個行為釘住，換 provider 或 EF 版本改變行為時會紅。

要留意的是這些函式**只存在於 EF Core 開的連線**。用 `sqlite3` CLI、DB browser 或手寫原生 SQL
查同一個檔案時，TEXT 會退回字典序比較，`"9"` 會大於 `"100"`。所以原生 SQL 不要碰數量欄位的比較與排序。

另外 round-trip 會保留小數位數（`30m` 存進去讀出來變 `30.0`），由 `NormalizedDecimalConverter` 處理。

## 授權

[MIT](LICENSE)
