# 製造業 ERP 系統

Clean Architecture 分層的製造業 ERP，含一個以 tool-use 驅動的 AI 助理模組（規劃中）。

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
  Erp.ArchitectureTests      分層邊界測試（Domain 不得碰 AI 或 EF Core）
docs/
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
| Phase 4 — 展示準備 | 未開始 |

### Phase 1 已完成的服務

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

這三條寫死在程式碼與測試裡，改動前先看 `docs/ai-assistant-module-plan-v2.md`：

1. **可行性計算一律以 `AvailableQty`（帳上 − 已保留）為基準**，不使用帳上庫存。用帳上庫存會把別張工單保留的料重複計入，導致「系統說夠、現場缺料」。
2. **`RequiredPerFinishedUnit` 的分母是最終成品一個單位**，多階 BOM 的中間階用量會逐層累乘。例如 `TV-100 → CHASSIS-02 ×3 → SCREW-05 ×4`，螺絲對成品的用量是 12 而不是 4。
3. **BOM 展開一律展到葉節點原料，不動用半成品既有庫存。** 這會低估可製造量但不會高估，對交期判斷是安全方向。

## AI 助理（Phase 2）

需要 Anthropic API 金鑰，設為環境變數即可：

```bash
export ANTHROPIC_API_KEY=sk-ant-...
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

### 三個實作上踩到的點

**中文逃逸**：SDK 內部用預設 JSON 編碼器，會把中文逃逸成 `\uXXXX`。系統提示詞、
工具說明、使用者問題都是中文，實測請求體積變成 2.28 倍（2038 → 893 字元），
而工具說明每一輪都重送。SDK 沒有序列化設定點，因此用 `DelegatingHandler`
在送出前重新序列化一次，JSON 語意不變。`AnthropicWireFormatTests` 守住這條。

**工具參數不是單一 JSON 值**：`BetaToolUseBlockParam.Input` 的型別是屬性字典，
把 `JsonElement` 直接丟進去編不過，回送 tool_use 時要展開。

**數量帶著沒有意義的小數尾巴**：SQLite 把 decimal 存成 TEXT，讀回來會保留原本的小數位數，
於是 `30m` 存進去再取出變成 `30.0`。LLM 可能照抄成「短少 120.0 件」，每個數字也多花 token。
`NormalizedDecimalConverter` 在序列化時去掉無意義的尾隨零，AI 工具與 REST 端點共用。

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

`ErpDbSeeder` 灌入的情境就是 `docs/ai-assistant-module-plan-v2.md` 第 5 節範例 2，
所有日期以執行當天為基準相對產生，資料不會過期。

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
  `AskAsync` 預留了加 `conversation_id` 的空間，Phase 3 再視情況實作。
- **`AnthropicLlmClient` 尚未對真實 Anthropic API 驗證過**。tool-use 迴圈由整組測試涵蓋，
  送出的 HTTP 請求內容也用本機假伺服器逐欄檢查過，但從未實際打過一次 Anthropic API
  （本機沒有金鑰）。第一次帶著真金鑰執行時，仍應人工確認一輪完整問答。
- **AI 助理沒有使用者權限隔離**：唯讀，但查得到全庫資料。擴充方式是在 `ToolDispatcher`
  注入呼叫者身分並下推到查詢服務。

## SQLite 的一個限制

EF Core 把 `decimal` 存成 TEXT，資料庫層無法正確比較或排序數量欄位。
因此所有對數量的比較、加總、排序都必須在載入到記憶體之後才做，
查詢條件只用字串、日期與列舉 —— 各 Repository 都遵守這條，改動時要留意。
