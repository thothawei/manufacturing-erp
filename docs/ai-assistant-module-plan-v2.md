# 製造業 ERP系統 — AI 助理模組規劃（v2）

> **這份文件現在的角色**（實作完成後補記，內文保持動工前的原貌）：
>
> - **第 1–4 節仍是現行的設計規範**，不是歷史。工具契約、庫存計算基準、
>   `required_per_finished_unit` 的語意、防幻覺機制都只在這裡有完整說明，
>   程式碼與測試共有九處註解指向這幾節。八個工具名與八個參數名都與實作一致（核對過）。
> - **第 5 節的範例數字是規劃時的示意，與實際種子資料不同**：
>   範例 1 寫最多可做 42 台，實際是 40 台；範例 2 寫淨缺料 120 片，實際是 130 片
>   （種子資料多了一張逾期工單，貢獻 10 片需求）。
>   實際會跑出來的數字見 `demo-and-design-notes.md`。
> - **第 7–8 節的路線圖與工時估算已經走完**，目前進度與後續工作見
>   `ai-assistant-module-plan-v3.md`。
>
> v2 修訂重點（相對 [v1](ai-assistant-module-plan-v1.md)）：
> 1. 明訂庫存計算基準為 `available_qty`，並在工具回傳中帶出 `basis` 欄位。
> 2. `required_per_unit` 更名為 `required_per_finished_unit`，消除多階 BOM 下的語意歧義。
> 3. 新增 ToolCatalog 與 Application Service 簽章的反射一致性測試（防靜默漂移）。
> 4. 明列「單輪對話」為已知限制，並規劃 `conversation_id` 的擴充點。
> 5. 修正錯字（幻覈 → 幻覺）。
>
> 本文件僅涵蓋「AI 助理模組」的規劃，傳統 ERP 核心模組（主檔/BOM/途程/工單/採購/入庫/成本/品管）沿用先前已確認的規劃，不重複展開。

---

## 1. 架構圖與 Clean Architecture 銜接方式

### 1.1 分層原則（最重要的一條紅線）

**Domain 層完全不知道 AI 這件事存在。** LLM 相關的一切（HTTP 呼叫、Prompt、Tool Schema、對話迴圈）都封裝在 Infrastructure 層的一個獨立子命名空間 `Infrastructure.AI` 裡，與 `Infrastructure.Persistence`（EF Core）平行存在。Application 層只新增一個「port」（介面），不參考任何 LLM SDK 或 HTTP 套件。

```
┌─────────────────────────────────────────────────────────────────┐
│ API 層                                                            │
│  AiAssistantController                                           │
│    POST /api/ai-assistant/ask  { question: string }              │
│    → 回傳 { answer: string }                                       │
└───────────────────────────┬───────────────────────────────────────┘
                             │ 呼叫介面（依賴反轉）
┌───────────────────────────▼───────────────────────────────────────┐
│ Application 層                                                    │
│  interface IAiAssistantService                                   │
│      Task<string> AskAsync(string question, CancellationToken)   │
│                                                                     │
│  既有的查詢類 Application Services（AI 的「工具」就是包這些）：       │
│    ItemMasterQueryService / InventoryQueryService                 │
│    BomExplosionService（多階展開）                                  │
│    WorkOrderProgressService / WorkOrderRiskService                │
│    MrpCalculationService（缺料試算）                                │
│    PurchasingQueryService（採購單狀態查詢）                          │
│    QualityInspectionQueryService（品管合格/不合格摘要）               │
└───────────────────────────┬───────────────────────────────────────┘
                             │ 被下層實作 / 被下層呼叫
┌───────────────────────────▼───────────────────────────────────────┐
│ Infrastructure 層                                                  │
│  Infrastructure.AI（新增，與 Infrastructure.Persistence 平行）      │
│                                                                     │
│   AiAssistantService : IAiAssistantService   ← Application 介面的實作│
│     └─ 內部跑「tool-use 迴圈」：                                     │
│        1. 組 system prompt + 使用者問題 + Tool Schema 送給 LLM       │
│        2. LLM 回 tool_use → ToolDispatcher 執行對應工具              │
│        3. 把工具結果包成 tool_result 再丟回 LLM                       │
│        4. 重複直到 LLM 回純文字（設上限，如 5 輪，防止失控迴圈）        │
│                                                                     │
│   ToolCatalog        —— 集中定義所有 Tool 的 JSON Schema             │
│   ToolDispatcher     —— tool 名稱 → 呼叫對應 Application Service      │
│   AnthropicLlmClient —— 純 HTTP 呼叫 Anthropic Messages API          │
│                        （之後要換 OpenAI/其他家，只要換這一個類別）      │
│                                                                     │
│  Infrastructure.Persistence（既有）                                 │
│    EF Core DbContext / Repository 實作                             │
└─────────────────────────────────────────────────────────────────┘
```

### 1.2 為什麼這樣切

- **Domain 零污染**：BOM、工單、庫存這些核心規則以後就算把 LLM 整套換掉（甚至拿掉 AI 助理），Domain 完全不用動，這也是被問「為什麼這樣分層」時講得出道理的地方。
- **Application 只認介面，不認 LLM**：`IAiAssistantService` 定義在 Application，實作在 Infrastructure，這跟現有的 `IWorkOrderRepository` 之類的依賴反轉模式完全一致，架構風格統一，不是為了 AI 另開一套規則。
- **AI 只能「唯讀查詢」，不能「異動資料」**：目前規劃的所有 Tool 都只呼叫查詢類服務，沒有一個工具會寫入資料庫。這是刻意的設計限制，可以清楚說明「AI 助理目前只做查詢與建議摘要，不具備下單/扣庫存等寫入權限」，降低幻覺風險也降低系統風險。
- **不讓 LLM 自己組 SQL**：所有工具都是「呼叫既有 Application Service 方法」，回傳結構化 JSON，LLM 拿到的永遠是後端算好的數字，不會有 LLM 自己拼 SQL 字串或自己做四則運算後謊報數字的空間。

---

## 2. 庫存計算基準（v2 新增，跨工具的統一前提）

多個工具都會碰到「還有多少料可以用」這個問題，若各自解讀會導致答案不一致。本專案統一規定：

| 名詞 | 定義 |
|---|---|
| `on_hand_qty` | 帳上實際庫存數量 |
| `reserved_qty` | 已被未完工工單保留（已發放但尚未領用）的數量 |
| `available_qty` | `on_hand_qty - reserved_qty`，可自由配置給新需求的數量 |

**規定：所有「能不能做 / 缺多少料」的計算，一律以 `available_qty` 為基準。**

理由：若以 `on_hand_qty` 計算，會把別張工單已保留的料重複計入，答案系統性偏樂觀，實務上會導致「系統說夠、現場卻缺料」。

為了讓 LLM 能把這個前提講給使用者聽，凡是做可用量判斷的工具（#3、#6），回傳 JSON 一律附帶 `basis` 欄位（目前固定為 `"available"`），system prompt 中要求回答時說明「以可用庫存（扣除已保留）計算」。

---

## 3. Tool / Function 清單

所有工具都是**唯讀查詢**，輸出皆為結構化 JSON，單位與欄位名稱明確標示，讓 LLM 沒有「模糊解讀空間」。

| # | 函式名稱 | 輸入參數 | 回傳格式（重點欄位） | 對應 Application 層服務 |
|---|---|---|---|---|
| 1 | `search_items` | `keyword: string` | `items: [{ item_code, item_name, item_type }]` | `ItemMasterQueryService.SearchByKeyword` |
| 2 | `get_item_inventory_status` | `item_code: string` | `{ item_code, item_name, unit, on_hand_qty, reserved_qty, available_qty, as_of }` | `InventoryQueryService.GetStock` |
| 3 | `check_material_sufficiency_for_item` | `item_code: string`, `planned_qty?: number` | `{ item_code, bom_version, basis, max_buildable_qty, sufficient_for_requested_qty?, shortage_components: [{component_code, component_name, required_per_finished_unit, available_qty, shortfall_qty}] }` | `BomExplosionService.CalculateMaxBuildable`（多階BOM逐階展開查庫存） |
| 4 | `get_work_order_progress` | `work_order_no: string` | `{ work_order_no, item_code, planned_qty, status, routing_steps: [{step_no, operation_name, planned_qty, completed_qty, status}], material_issue_status }` | `WorkOrderProgressService.GetProgress` |
| 5 | `list_work_orders_at_risk` | `date_range_start?`, `date_range_end?`（預設本週） | `{ work_orders: [{work_order_no, item_code, due_date, delay_days, risk_reason}] }` | `WorkOrderRiskService.GetAtRiskWorkOrders`（比對途程實際進度 vs 排程 + 物料是否足夠） |
| 6 | `run_mrp_shortage_analysis` | `planning_horizon_days?`, `item_code?`（篩選用） | `{ basis, shortage_items: [{item_code, item_name, net_shortage_qty, needed_by_date, suggested_order_qty, supplier_code, lead_time_days}] }` | `MrpCalculationService.RunShortageAnalysis` |
| 7 | `list_open_purchase_orders` | `supplier_code?`, `item_code?` | `{ purchase_orders: [{po_no, supplier_code, item_code, ordered_qty, received_qty, expected_arrival_date, status}] }` | `PurchasingQueryService.GetOpenPurchaseOrders` |
| 8 | `get_quality_inspection_summary` | `item_code?`, `work_order_no?`, `date_range_start?`, `date_range_end?` | `{ inspections: [{work_order_no, item_code, inspected_qty, passed_qty, failed_qty, fail_reason_summary}] }` | `QualityInspectionQueryService.GetSummary` |

### 3.1 `required_per_finished_unit` 的語意（v2 修訂）

`shortage_components` 是多階 BOM **攤平後**的清單，因此用量必須以「最終成品一個單位」為分母，而不是「直接上階父件一個單位」。

以本文件的範例結構為例：

```
TV-100 (成品)
├── PANEL-01   × 2
└── CHASSIS-02 × 3
    └── SCREW-05 × 4
```

- SCREW-05 對其直接父件 CHASSIS-02 的用量是 **4**
- SCREW-05 對最終成品 TV-100 的用量是 **3 × 4 = 12** ← 這才是 `required_per_finished_unit` 要放的值

v1 原本命名為 `required_per_unit`，在單階 BOM 下兩種解讀剛好同值、看不出差異，一旦上階用量不是 1 就會出錯，且 LLM 極可能直接把它當「每台成品用量」講給使用者。故 v2 更名並要求：**多階單元測試必須刻意讓中間階用量不等於 1**，否則測試無法分辨兩種語意。

---

## 4. 防幻覺 / 防編造數字的保險

1. **結構化輸出**：每個工具都回傳固定 schema 的 JSON，數值一律來自 Application 層計算結果，LLM 不會拿到「一段模糊文字」去自己腦補數字。
2. **System Prompt 硬性規則**：明確告知 LLM「所有數字（庫存量、缺料量、延遲天數等）只能來自工具回傳結果，禁止自行推算或估計；若工具回傳為空或發生錯誤，必須如實告知使用者查無資料，不可編造」。
3. **架構測試（NetArchTest）**：驗證 `Domain` 專案沒有任何對 `Infrastructure.AI` 或 LLM SDK 套件的參考（這條可以直接寫成一個永遠會跑的 CI 測試，展示「架構邊界是被測試保護的，不是嘴巴講講」）。
4. **Mock LLM 單元測試**：xUnit + mock `ILlmClient`，固定回傳某個 `tool_use` 回應，驗證 `ToolDispatcher` 是否正確路由到對應 Application Service、組出的 `tool_result` 格式是否正確。
5. **ToolCatalog 一致性測試（v2 新增）**：`ToolCatalog` 的 JSON Schema 是手寫的，`ToolDispatcher` 以字串比對參數名。若日後 `MrpCalculationService` 改了參數名稱，C# 編譯不會失敗，只有實際執行到該工具時才會炸。因此新增一個測試：走訪 `ToolCatalog` 的每個工具定義，用反射取出對應 Application Service 方法的參數名稱與必填性，逐一比對。這比 mock LLM 測試更能防真實故障。

---

## 5. 範例對話流程

### 範例 1：自然語言查詢（庫存是否足夠生產）

> 使用者：「TV-100 這個成品，用現有庫存最多可以做幾台？」

1. AI 呼叫 `search_items(keyword="TV-100")` → 確認唯一匹配到 `item_code=TV-100`，且為成品。
2. AI 呼叫 `check_material_sufficiency_for_item(item_code="TV-100")`（未指定 `planned_qty`，代表要問「最多能做幾台」）。
3. `BomExplosionService` 內部展開 TV-100 的多階 BOM，逐階以 `available_qty` 計算，找出限制產出的瓶頸原料，回傳：
   ```json
   { "item_code": "TV-100", "bom_version": "v3", "basis": "available", "max_buildable_qty": 42, "shortage_components": [] }
   ```
4. LLM 摘要成自然語言回覆：「以目前可用庫存（已扣除其他工單保留量），TV-100 最多可以生產 42 台，沒有原物料瓶頸。」

若瓶頸存在（`shortage_components` 非空），LLM 會如實列出是哪個零件卡住、還缺多少，而不是只回一個總數。

### 範例 2：缺料預警 + 採購建議

> 使用者：「這週有哪些工單有延遲風險？如果缺料的話幫我列建議採購清單。」

1. AI 呼叫 `list_work_orders_at_risk()`（預設本週），取得：
   ```json
   { "work_orders": [
     { "work_order_no": "WO-20260910-03", "item_code": "TV-100", "due_date": "2026-09-15", "delay_days": 2, "risk_reason": "缺料：PANEL-01 短少 120 件" }
   ]}
   ```
2. LLM 先摘要風險工單清單給使用者。
3. 因使用者同時要求採購建議，AI 接著呼叫 `run_mrp_shortage_analysis()`，取得：
   ```json
   { "basis": "available", "shortage_items": [
     { "item_code": "PANEL-01", "item_name": "面板", "net_shortage_qty": 120, "needed_by_date": "2026-09-13", "suggested_order_qty": 150, "supplier_code": "SUP-008", "lead_time_days": 5 }
   ]}
   ```
4. LLM 組出建議文字：「本週風險工單 WO-20260910-03（TV-100）預估延遲 2 天，主因是面板（PANEL-01）短少 120 件。建議優先向供應商 SUP-008 下單採購 150 件，因交期需 5 天，若不儘快下單將影響如期交貨。」

注意：120、150、5 天、2 天全部原封不動來自工具回傳的數字，LLM 只負責組句子與加上「建議優先」這類語氣詞，沒有自己計算或臆測任何數值。

---

## 6. 已知限制（v2 新增）

| 限制 | 影響 | 處理方式 |
|---|---|---|
| **單輪對話，無上下文** | API 契約是 `{question} → {answer}`，無對話歷史。使用者追問「那 SUP-008 上次交期準嗎」時，AI 不知道 SUP-008 從何而來 | 明列為已知限制。擴充點：`AskAsync` 已預留可加入 `conversation_id` 參數，Phase 3 視時間決定是否實作（伺服器端存放訊息歷史即可，不需改動 Tool 層） |
| **無使用者權限 / 租戶隔離** | AI 雖唯讀，但查得到全庫資料，任何登入者都能問到所有工單與成本相關資訊 | 目前定位為單一公司內部工具，不做多租戶。被問到時，說明擴充方式是在 `ToolDispatcher` 注入呼叫者身分並下推至查詢服務 |
| **LLM 呼叫無成本控管** | 未記錄 token 用量、無逾時與重試策略 | Phase 2 在 `AnthropicLlmClient` 實作逾時與有限次重試；token 用量記錄為 Phase 3 選配 |

---

## 7. 分階段開發路線圖

| 階段 | 內容 | 說明 |
|---|---|---|
| Phase 0 | 傳統 ERP MVP：客戶訂單 → 單階 BOM → 工單 → 領料扣庫存 → 完工入庫 | 核心資料模型與業務邏輯，有測試覆蓋。**MVP 尚未做多階 BOM 與 MRP** |
| Phase 1 | 補齊 AI 模組依賴的查詢類服務：多階 BOM 展開（`BomExplosionService`）、工單風險判定（`WorkOrderRiskService`）、MRP 缺料試算（`MrpCalculationService`）、採購單查詢（`PurchasingQueryService`）、品管摘要查詢（`QualityInspectionQueryService`） | 這五個是 AI 工具背後真正做計算/查詢的邏輯，AI 只是包一層介面去呼叫，所以要先在 Application（+ 必要的 Domain 實體）做出來並寫好單元測試 |
| Phase 2 | AI 基礎建設打通：`Infrastructure.AI`（`AnthropicLlmClient`、`ToolCatalog`、`ToolDispatcher`、tool-use 迴圈）、`IAiAssistantService` 介面、API endpoint | 先只接 1–2 個工具（`search_items` + `get_item_inventory_status`）打通端到端流程 |
| Phase 3 | 補完剩餘工具、防幻覺機制與測試 | 補齊全部 8 個工具、system prompt 調校、工具呼叫失敗的錯誤處理、mock LLM 測試、架構測試、ToolCatalog 一致性測試 |
| Phase 4 | 展示準備 | 示範對話腳本、curl/Postman 範例、README 說明架構決策 |

---

## 8. 預估工作階段數

- Phase 1（五項查詢/計算邏輯）：約 3–5 次工作階段
- Phase 2（AI 基礎建設打通，含第一個工具端到端跑通）：約 2–3 次工作階段
- Phase 3（補完全部 8 個工具 + 防幻覺與測試）：約 4–5 次工作階段
- Phase 4（展示腳本與文件打磨）：約 1–2 次工作階段

**AI 助理模組本身總計約 10–15 次工作階段。**
