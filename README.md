# 製造業 ERP 系統

[![CI](https://github.com/thothawei/manufacturing-erp/actions/workflows/ci.yml/badge.svg)](https://github.com/thothawei/manufacturing-erp/actions/workflows/ci.yml)

Clean Architecture 分層的製造業 ERP，含一個以 tool-use 驅動的 AI 助理：
使用者用自然語言提問，AI 透過八個唯讀工具查詢系統資料後回答，所有數字都由後端算好。

166 個測試，0 警告。**唯一未驗證的環節**：`AnthropicLlmClient` 從未對真實 Anthropic API
發過請求（開發機沒有金鑰），詳見「尚未處理」。

| 想看什麼 | 去哪裡 |
|---|---|
| 三十秒跑起來、實際輸出、面試問答 | [`docs/demo-and-interview.md`](docs/demo-and-interview.md) |
| 規劃與實作的逐條對帳、剩餘工作 | [`docs/ai-assistant-module-plan-v3.md`](docs/ai-assistant-module-plan-v3.md) |
| 架構決策與踩過的坑 | 本文件以下各節 |

## 專案結構

```
src/
  Erp.Domain          實體與領域規則，不依賴任何外部套件
  Erp.Application     使用案例服務 + Repository 介面與 IAiAssistantService（port）
  Erp.Infrastructure  Persistence（EF Core）與 AI（Anthropic tool-use）兩個平行子系統
  Erp.Api             HTTP 端點
tests/
  Erp.Application.Tests      Application 層單元測試（以 in-memory 假 Repository 驅動）
  Erp.Infrastructure.Tests   EF Core 整合測試、tool-use 迴圈測試、Anthropic wire format 測試
  Erp.Api.Tests              HTTP 端點測試（例外 → 狀態碼對映）
  Erp.ArchitectureTests      分層邊界測試（Domain 不得碰 AI 或 EF Core）
docs/
  demo-and-interview.md            展示腳本與面試問答
  ai-assistant-module-plan-v3.md   現行規劃：實作對帳與剩餘工作
  ai-assistant-module-plan-v2.md   動工前的設計規劃（歷史）
```

Domain 完全不知道 AI 的存在：`IAiAssistantService` 定義在 Application，實作在 `Infrastructure/AI`，
與 `Infrastructure/Persistence` 平行，跟既有的 Repository 一樣是依賴反轉。

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

啟動 API（開發模式會自動建表並灌入展示資料）：

```bash
dotnet run --project src/Erp.Api --urls http://localhost:5199
```

資料庫是 SQLite 檔（`src/Erp.Api/erp.db`），刪掉再啟動就會重新產生一份乾淨的展示資料。

## 目前進度

| 階段 | 狀態 |
|---|---|
| Phase 1 — AI 工具背後的查詢／計算服務 | 完成，含 EF Core 資料層與種子資料 |
| Phase 2 — Infrastructure.AI 與 tool-use 迴圈 | 完成；**尚未對真實 API 驗證過**（見下方） |
| Phase 3 — 補完 8 個工具、架構測試與防幻覺測試 | 完成 |
| Phase 3.5 — 規劃對帳後補齊的缺口（錯誤處理、錯誤碼、稽核 log、user-secrets） | 完成 |
| Phase 4 — 展示準備 | 完成（`docs/demo-and-interview.md`）；真實 API 驗證待金鑰 |

### 八個 Application 服務（AI 工具背後真正做事的地方）

| 服務 | 職責 |
|---|---|
| `ItemMasterQueryService` | 料號／品名關鍵字搜尋 |
| `InventoryQueryService` | 帳上／保留／可用庫存查詢 |
| `BomExplosionService` | 多階 BOM 展開、以可用庫存試算最大可製造量與缺料件 |
| `WorkOrderProgressService` | 工單途程進度查詢 |
| `WorkOrderRiskService` | 工單延遲風險判定（逾期 + 缺料兩種來源） |
| `MrpCalculationService` | MRP 缺料試算與建議採購量 |
| `PurchasingQueryService` | 未結案採購單查詢 |
| `QualityInspectionQueryService` | 品管檢驗結果彙總 |

## 三個必須知道的計算約定

這三條寫死在程式碼與測試裡，改動前先看 [`docs/ai-assistant-module-plan-v3.md`](docs/ai-assistant-module-plan-v3.md)：

1. **可行性計算一律以 `AvailableQty`（帳上 − 已保留）為基準**，不使用帳上庫存。用帳上庫存會把別張工單保留的料重複計入，導致「系統說夠、現場缺料」。
2. **`RequiredPerFinishedUnit` 的分母是最終成品一個單位**，多階 BOM 的中間階用量會逐層累乘。例如 `TV-100 → CHASSIS-02 ×3 → SCREW-05 ×4`，螺絲對成品的用量是 12 而不是 4。
3. **BOM 展開一律展到葉節點原料，不動用半成品既有庫存。** 這會低估可製造量但不會高估，對交期判斷是安全方向。

## AI 助理

需要 Anthropic API 金鑰。本機開發用 user-secrets（存在專案外，不可能被誤 commit）：

```bash
dotnet user-secrets set "AiAssistant:ApiKey" "sk-ant-..." --project src/Erp.Api
```

macOS 上也可以先把金鑰複製到剪貼簿，再執行 `./scripts/set-api-key.sh` ——
它會檢查前綴與長度後才寫入，避免把錯的東西存進去（金鑰不會顯示在畫面或 shell history）。

或用環境變數（正式環境的做法）：

```bash
export ANTHROPIC_API_KEY=sk-ant-...
```

兩者都不要寫進 `appsettings.json`。啟動時會印出生效的設定與金鑰來源（只印來源、不印值）：

```
AI 助理設定：模型 claude-opus-5，工具迴圈上限 5 輪，逾時 60 秒，API 金鑰來源：設定檔或 user-secrets
```

```bash
dotnet run --project src/Erp.Api --urls http://localhost:5199
```

```bash
curl -X POST http://localhost:5199/api/ai-assistant/ask -H 'Content-Type: application/json' -d '{"question":"面板還有多少可以用？"}'
```

沒設金鑰時回 503 與清楚訊息，不會洩漏 SDK 堆疊。

### 八個工具

全部都是唯讀查詢，沒有一個會寫入資料庫。每個工具都只是薄薄一層，
把參數轉交給既有的 Application Service，數字一律由後端算好。

| 工具 | 對應服務 |
|---|---|
| `search_items` | `ItemMasterQueryService` |
| `get_item_inventory_status` | `InventoryQueryService` |
| `check_material_sufficiency_for_item` | `BomExplosionService` |
| `get_work_order_progress` | `WorkOrderProgressService` |
| `list_work_orders_at_risk` | `WorkOrderRiskService` |
| `run_mrp_shortage_analysis` | `MrpCalculationService` |
| `list_open_purchase_orders` | `PurchasingQueryService` |
| `get_quality_inspection_summary` | `QualityInspectionQueryService` |

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

## API 錯誤處理

`ErpExceptionHandler` 把 Application 層的例外對映成語意正確的狀態碼。
沒有這一層時，查無料號會回 500，而且回應體直接吐出完整堆疊與本機絕對路徑。

| 例外 | 狀態碼 |
|---|---|
| `EntityNotFoundException` | 404 |
| `ArgumentException`（含 `ArgumentOutOfRangeException`） | 400 |
| `InvalidOperationException` | 409（參數合法但操作不適用，如對原物料問可製造量） |
| `LlmUnavailableException` | 503 |
| 其他 | 500，回應只有通用訊息，全文進伺服器 log |

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

**未預期例外會炸掉整段對話**：`ToolDispatcher` 原本只攔三類已知例外，資料庫連線失效
會穿過 tool-use 迴圈變成 HTTP 500，即使同一輪其他工具的結果是好的也一起陣亡。
現在降級成單一工具的失敗。

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

**工具參數不是單一 JSON 值**：`BetaToolUseBlockParam.Input` 的型別是屬性字典，
把 `JsonElement` 直接丟進去編不過，回送 tool_use 時要展開。

## 測試策略

166 個測試，分四個專案：

| 專案 | 數量 | 涵蓋 |
|---|---|---|
| `Erp.Application.Tests` | 43 | 計算邏輯（多階 BOM、風險判定、MRP），用 in-memory 假 Repository |
| `Erp.Infrastructure.Tests` | 100 | EF Core 整合、tool-use 迴圈、錯誤契約、稽核 log、Anthropic wire format |
| `Erp.Api.Tests` | 13 | HTTP 端點的錯誤對映與正常路徑（`WebApplicationFactory`） |
| `Erp.ArchitectureTests` | 7 | 分層邊界 |

幾個值得一提的：

- **`ToolCatalogConsistencyTests`** — 工具的 JSON Schema 是手寫的，`ToolDispatcher` 用字串
  比對參數名，兩邊漂掉時 C# 編譯不會失敗。這組測試走訪目錄裡每個工具，
  **連選填參數都真的帶進去執行一次**（只測必填的話，選填參數改名不會被發現）。
- **`AnthropicWireFormatTests`** — 用本機假伺服器接住 SDK 真正送出的 HTTP 請求，
  逐欄檢查 body。不需要金鑰、不花錢。型別轉換編譯得過不代表 wire format 正確。
- **`QueryEfficiencyTests`** — 斷言查詢次數如何「隨缺料料號數成長」，而不是絕對次數
  （那會隨 BOM 結構改變，只會製造脆弱的測試）。把 N+1 改回去會紅。
- **`SystemPromptTests`** — 明確**不驗證 LLM 是否遵守規則**（那需要真實 API 與行為評測），
  防的是有人重寫 prompt 時把某條規則整個刪掉。

### 每條防線都做過反向驗證

把防線拔掉、確認測試會紅，再還原。沒有紅過的測試等於沒有測試。
八條防線的驗證結果列在 [`docs/demo-and-interview.md`](docs/demo-and-interview.md) 的面試問答一節。

這個習慣抓到過一次自己的錯誤：架構測試第一次反向驗證是綠的，一度以為測試無效，
深挖後發現是實驗寫錯 —— `nameof` 是編譯期常數不留型別參考，改用 `typeof` 就紅了。

## 架構邊界

`Erp.ArchitectureTests` 讓分層規則被 CI 保護，而不是靠自律：

- Domain 不得相依於任何其他層，也不得參考 EF Core
- Application 不得相依於 Infrastructure（依賴反轉的方向）
- Domain 與 Application 都不得參考任何 LLM 廠商套件
- Persistence 與 AI 是平行子系統，Persistence 不得相依於 AI

其中「不得參考 LLM 廠商套件」用的是組件參考檢查而不是命名空間規則 ——
NetArchTest 檢查的是 `Erp.*` 命名空間，對外部套件無感。實測在 Domain 裡寫
`typeof(Anthropic.AnthropicClient)` 時，只有組件參考那條會紅。
（注意 `nameof` 是編譯期常數，不會在 IL 留下型別參考，用它測不出違規。）

## 展示資料

`ErpDbSeeder` 灌入的情境所有日期與單號都以執行當天為基準相對產生，資料不會過期。
完整的展示腳本與每個數字的看點在 [`docs/demo-and-interview.md`](docs/demo-and-interview.md)。

產品結構：

```
TV-100  ├── PANEL-01   × 2
        ├── CHASSIS-02 × 3 ── SCREW-05 × 4   （螺絲對成品 = 12 支）
        └── CABLE-07   × 1
```

情境重點：面板帳上 100 片、保留 20 片，可用只有 80 片。

```bash
curl "http://localhost:5199/api/items/TV-100/sufficiency"    # 最多做 40 台（用帳上庫存會誤算成 50 台）
curl "http://localhost:5199/api/work-orders/at-risk"          # 兩張風險工單，各延遲 2 天
curl "http://localhost:5199/api/mrp/shortages"                # 面板淨缺 130 片，建議下單 150 片
```

這些數字都被 `SeededScenarioTests` 釘住，改動種子資料而沒同步更新文件時測試會先紅。

## 尚未處理

- **MRP 沒有時間分桶（time-phasing）**：同一料號的需求日一律取最早的那張工單，
  若最急的是一張小需求，整批需求都會被貼上該日期，建議採購會偏保守。
- **MRP 假設工單需求尚未反映在 `ReservedQty`**；工單若已實際發料會高估需求量（方向偏保守）。
- **BOM 展開是逐階查詢**：每個節點一次資料庫往返，深層 BOM 會放大成本。
  正確解法是一次載入整棵樹或改用遞迴 CTE，目前資料量下不構成問題。
  （採購單與補料條件的 N+1 已消除，由 `QueryEfficiencyTests` 把關。）
- **工單風險的預設區間是本週**：逾期超過一週且未結案的工單不會出現在預設查詢中，
  需自行指定 `from`。MRP 則已把所有逾期未結案工單納入。
- **AI 助理為單輪問答**，無對話上下文。追問「那它的供應商是誰」時，助理不知道「它」指什麼。
  `AskAsync` 預留了加 `conversation_id` 的空間，尚未實作。
- **`AnthropicLlmClient` 尚未對真實 Anthropic API 驗證過**。tool-use 迴圈由整組測試涵蓋，
  送出的 HTTP 請求內容也用本機假伺服器逐欄檢查過，但從未實際打過一次 Anthropic API
  （本機沒有金鑰）。第一次帶著真金鑰執行時，仍應人工確認一輪完整問答。
- **AI 助理沒有使用者權限隔離**：唯讀，但查得到全庫資料。擴充方式是在 `ToolDispatcher`
  注入呼叫者身分並下推到查詢服務。

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
