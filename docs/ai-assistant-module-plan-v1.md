# 製造業 ERP系統 — AI 助理模組規劃（v1）

> **這是最初的規劃，動工前的版本**，內文大致保持原貌（含當時的錯字與後來被推翻的決定）。
> 唯一的改動是把幾處提到求職情境的措辭換成中性說法，技術內容一字未動。
>
> - **第 6 節是 v3 裡 G1–G6 的來源**：那七項「補齊的規劃缺口」在實作對帳時
>   被逐條檢查，六項當時沒做到的後來都補上了。
> - **與實作不符的地方都已對帳，不需要在這裡修正**：純 HTTP 呼叫改用官方 SDK、
>   採購單維持單行而非單頭單身、錯誤碼用 `ENTITY_NOT_FOUND` 而非 `ITEM_NOT_FOUND`、
>   資料庫用 SQLite 而非 SQL Server —— 每一項的理由與決策見
>   `ai-assistant-module-plan-v3.md` 第 2 節。
> - **原文第 7 節（Phase 1 啟動 prompt）未收錄**。
> - 修訂版見 `ai-assistant-module-plan-v2.md`，實作進度與對帳見 v3。


> 本文件僅涵蓋「AI 助理模組」的規劃，傳統 ERP 核心模組（主檔/BOM/途程/工單/採購/入庫/成本/品管）沿用先前已確認的規劃，不重複展開。本階段**不寫程式碼**，待你確認範圍與設計後再逐步實作。

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
- **AI 只能「唯讀查詢」，不能「異動資料」**：目前規劃的所有 Tool 都只呼叫查詢類服務，沒有一個工具會寫入資料庫。這是刻意的設計限制，可以清楚說明「AI 助理目前只做查詢與建議摘要，不具備下單/扣庫存等寫入權限」，降低幻覈風險也降低系統風險。
- **不讓 LLM 自己組 SQL**：所有工具都是「呼叫既有 Application Service 方法」，回傳結構化 JSON，LLM 拿到的永遠是後端算好的數字，不會有 LLM 自己拼 SQL 字串或自己做四則運算後謊報數字的空間。

---

## 2. Tool / Function 清單

所有工具都是**唯讀查詢**，輸出皆為結構化 JSON，單位與欄位名稱明確標示，讓 LLM 沒有「模糊解讀空間」。

| # | 函式名稱 | 輸入參數 | 回傳格式（重點欄位） | 對應 Application 層服務 |
|---|---|---|---|---|
| 1 | `search_items` | `keyword: string` | `items: [{ item_code, item_name, item_type }]` | `ItemMasterQueryService.SearchByKeyword` |
| 2 | `get_item_inventory_status` | `item_code: string` | `{ item_code, item_name, unit, on_hand_qty, reserved_qty, available_qty, as_of }` | `InventoryQueryService.GetStock` |
| 3 | `check_material_sufficiency_for_item` | `item_code: string`, `planned_qty?: number` | `{ item_code, bom_version, max_buildable_qty, sufficient_for_requested_qty?, shortage_components: [{component_code, component_name, required_per_unit, available_qty, shortfall_qty}] }` | `BomExplosionService.CalculateMaxBuildable`（多階BOM逐階展開查庫存） |
| 4 | `get_work_order_progress` | `work_order_no: string` | `{ work_order_no, item_code, planned_qty, status, routing_steps: [{step_no, operation_name, planned_qty, completed_qty, status}], material_issue_status }` | `WorkOrderProgressService.GetProgress` |
| 5 | `list_work_orders_at_risk` | `date_range_start?`, `date_range_end?`（預設本週） | `{ work_orders: [{work_order_no, item_code, due_date, delay_days, risk_reason}] }` | `WorkOrderRiskService.GetAtRiskWorkOrders`（比對途程實際進度 vs 排程 + 物料是否足夠） |
| 6 | `run_mrp_shortage_analysis` | `planning_horizon_days?`, `item_code?`（篩選用） | `{ shortage_items: [{item_code, item_name, net_shortage_qty, needed_by_date, suggested_order_qty, supplier_code, lead_time_days}] }` | `MrpCalculationService.RunShortageAnalysis` |
| 7 | `list_open_purchase_orders` | `supplier_code?`, `item_code?` | `{ purchase_orders: [{po_no, supplier_code, item_code, ordered_qty, received_qty, expected_arrival_date, status}] }` | `PurchasingQueryService.GetOpenPurchaseOrders` |
| 8 | `get_quality_inspection_summary` | `item_code?`, `work_order_no?`, `date_range_start?`, `date_range_end?` | `{ inspections: [{work_order_no, item_code, inspected_qty, passed_qty, failed_qty, fail_reason_summary}] }` | `QualityInspectionQueryService.GetSummary` |

**防幻覺 / 防編造數字的三道保險：**

1. **結構化輸出**：每個工具都回傳固定 schema 的 JSON，數值一律來自 Application 層計算結果，LLM 不會拿到「一段模糊文字」去自己腦補數字。
2. **System Prompt 硬性規則**：明確告知 LLM「所有數字（庫存量、缺料量、延遲天數等）只能來自工具回傳結果，禁止自行推算或估計；若工具回傳為空或發生錯誤，必須如實告知使用者查無資料，不可編造」。
3. **架構測試（NetArchTest）+ 單元測試**：
   - xUnit + mock `ILlmClient`：固定回傳某個 `tool_use` 回應，驗證 `ToolDispatcher` 是否正確路由到對應 Application Service、組出的 `tool_result` 格式是否正確。
   - 架構測試：驗證 `Domain` 專案沒有任何對 `Infrastructure.AI` 或 LLM SDK 套件的參考（這條可以直接寫成一個永遠會跑的 CI 測試，展示「架構邊界是被測試保護的，不是嘴巴講講」）。

8 個工具皆納入本次規劃範圍，`list_open_purchase_orders` 與 `get_quality_inspection_summary` 依賴的採購/品管查詢邏輯將與多階 BOM 展開、MRP 試算一起在 Phase 1 補齊（詳見第 4 節）。

---

## 3. 範例對話流程

### 範例 1：自然語言查詢（庫存是否足夠生產）

> 使用者：「TV-100 這個成品，用現有庫存最多可以做幾台？」

1. AI 呼叫 `search_items(keyword="TV-100")` → 確認唯一匹配到 `item_code=TV-100`，且為成品。
2. AI 呼叫 `check_material_sufficiency_for_item(item_code="TV-100")`（未指定 `planned_qty`，代表要問「最多能做幾台」）。
3. `BomExplosionService` 內部展開 TV-100 的多階 BOM（例如 TV-100 = 2×PANEL-01 + 1×CHASSIS-02，CHASSIS-02 又需要 4×SCREW-05…），逐階查即時庫存，找出限制產出的瓶頸原料，回傳：
   ```json
   { "item_code": "TV-100", "bom_version": "v3", "max_buildable_qty": 42, "shortage_components": [] }
   ```
4. LLM 摘要成自然語言回覆：「以目前庫存，TV-100 最多可以生產 42 台，沒有原物料瓶頸。」

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
   { "shortage_items": [
     { "item_code": "PANEL-01", "item_name": "面板", "net_shortage_qty": 120, "needed_by_date": "2026-09-13", "suggested_order_qty": 150, "supplier_code": "SUP-008", "lead_time_days": 5 }
   ]}
   ```
4. LLM 組出建議文字：「本週風險工單 WO-20260910-03（TV-100）預估延遲 2 天，主因是面板（PANEL-01）短少 120 件。建議優先向供應商 SUP-008 下單採購 150 件，因交期需 5 天，若不儘快下單將影響如期交貨。」

注意：120、150、5 天、2 天全部原封不動來自工具回傳的數字，LLM 只負責組句子與加上「建議優先」這類語氣詞，沒有自己計算或臆測任何數值。

---

## 4. 分階段開發路線圖

| 階段 | 內容 | 說明 |
|---|---|---|
| Phase 0（已規劃，優先完成） | 傳統 ERP MVP：客戶訂單 → 單階 BOM → 工單 → 領料扣庫存 → 完工入庫 | 沿用先前規劃，AI 模組排在此之後，先確保核心資料模型與業務邏輯正確、有測試覆蓋。**目前 MVP 尚未做多階 BOM 與 MRP**，故 Phase 1 範圍會比較大 |
| Phase 1 | 補齊 AI 模組依賴的查詢類服務（目前都還沒做，須新建）：多階 BOM 展開（`BomExplosionService`）、工單風險判定（`WorkOrderRiskService`）、MRP 缺料試算（`MrpCalculationService`）、採購單查詢（`PurchasingQueryService`，含最基本的採購單資料模型）、品管摘要查詢（`QualityInspectionQueryService`，含合格/不合格標記資料模型） | 這五個是 AI 工具背後真正做計算/查詢的邏輯，AI 只是包一層介面去呼叫，所以要先在 Application（+ 必要的 Domain 實體）做出來並寫好單元測試。範圍比只做 BOM/MRP 大，估算已反映在下方 |
| Phase 2 | AI 基礎建設打通：`Infrastructure.AI`（`AnthropicLlmClient`、`ToolCatalog`、`ToolDispatcher`、tool-use 迴圈）、`IAiAssistantService` 介面、API endpoint | 先只接 1–2 個工具（`search_items` + `get_item_inventory_status`）打通端到端流程，確認 tool-use 迴圈可正常運作 |
| Phase 3 | 補完剩餘工具、防幻覺機制與測試 | 補齊全部 8 個工具、system prompt 調校、工具呼叫失敗的錯誤處理、xUnit mock LLM 測試、架構測試 |
| Phase 4 | 展示準備 | 準備示範對話腳本、curl/Postman 範例、README 說明架構決策（為什麼 Domain 不碰 AI、為什麼工具都是唯讀） |

---

## 5. 預估工作階段數

以 ERP MVP（Phase 0：客戶訂單→單階BOM→工單→領料→入庫）已經穩定完成、但**多階 BOM／MRP／採購／品管邏輯尚未開始**為前提：

- Phase 1（新建多階 BOM 展開、工單風險判定、MRP 試算、採購單查詢、品管摘要查詢五項邏輯）：約 3–5 次工作階段
- Phase 2（AI 基礎建設打通，含第一個工具端到端跑通）：約 2–3 次工作階段
- Phase 3（補完全部 8 個工具 + 防幻覺與測試）：約 4–5 次工作階段
- Phase 4（展示腳本與文件打磨）：約 1–2 次工作階段

**AI 助理模組本身總計約 10–15 次工作階段**（比僅做 6 個工具、且 BOM/MRP 已完成的情境多，因為這次把採購單與品管也一併納入，且這些底層邏輯都要從零建立）。

---

## 已確認的範圍

1. Phase 0 的 MVP 目前**只有單階 BOM**，多階 BOM 展開、MRP 缺料試算尚未開始 → 已反映在 Phase 1 與工時估算中。
2. 工具清單採用**全部 8 個**（含採購單狀態、品管摘要），採購/品管的底層查詢邏輯與 BOM/MRP 一起排在 Phase 1 建置。

以上範圍已確認，之後即可依 Phase 0 → Phase 1 → Phase 2 → Phase 3 → Phase 4 的順序逐步實作；每次只推進一個 Phase 內的一小步，不會一次把所有程式碼生成出來。

---

## 6. 補齊的規劃缺口

重新檢視前一版規劃，以下幾點原本沒講清楚，這次一併補上。除非你有不同意見，否則採用下列預設決策繼續往下走。

### 6.1 API 合約：單輪問答，不做跨請求的對話記憶

你原始需求是「傳文字問題進去，回傳文字答案」的簡單 endpoint，所以維持**單輪、無狀態**：

```
POST /api/ai-assistant/ask
Request:  { "question": "TV-100 用現有庫存最多可以做幾台？" }
Response: { "answer": "以目前庫存，TV-100 最多可以生產 42 台，沒有原物料瓶頸。" }
```

要注意的是：**一次 HTTP request 內部仍然會有多輪 LLM ↔ 工具往返**（第 1 節講的 tool-use 迴圈），這跟「使用者跨多次請求的對話記憶」是兩件事。伺服器不保存對話歷史、不做 session／Conversation 資料表，如果之後要做多輪對話，屬於明確排除在本次規劃之外的「未來擴充項」，不影響目前的分層與工具設計。

### 6.2 模糊比對與錯誤處理規則

之前只講了「AI 不能編造數字」，但沒講清楚「查不到 / 查到多筆 / 工具報錯」這三種情況要怎麼處理：

- **`ToolDispatcher` 統一的錯誤回傳格式**：Application 層拋出已知例外（如 `ItemNotFoundException`）時，`ToolDispatcher` 攔截後包成 `tool_result` 並標記 `is_error: true`，內容為 `{ "error_code": "ITEM_NOT_FOUND", "message": "查無此料號" }`；未預期的例外一律記錄伺服器端 log，回給 LLM 一個通用的 `{ "error_code": "INTERNAL_ERROR" }`，絕不把堆疊或內部訊息丟給 LLM。
- **`search_items` 回傳多筆的處理規則**：寫進 system prompt 強制規定——若比對到多筆結果，AI 必須先列出選項請使用者確認料號，不可自行挑一筆往下查（這也是防止 AI 用錯資料亂答的關鍵一環）。
- **超出範圍的問題**：若使用者問的東西跟料號/庫存/工單/採購/品管無關，system prompt 規定 AI 要禮貌說明「只能協助 ERP 相關查詢」，不強行回答。

### 6.3 採購單／品管：只做 AI 工具需要的最小資料模型

第 7、8 號工具依賴的採購／品管邏輯目前完全沒有，但**這裡只補「AI 查詢用得到的最小欄位」，不是把完整採購/品管模組做出來**（完整流程屬於原本「傳統 ERP 核心模組」規劃，不在這次 AI 規劃範圍內重做）：

- `PurchaseOrder`：`Id, PoNo, SupplierId, Status(Draft/Approved/PartiallyReceived/Closed), OrderDate`
- `PurchaseOrderLine`：`Id, PurchaseOrderId, ItemId, OrderedQty, ReceivedQty, ExpectedArrivalDate`
- `QualityInspectionRecord`：`Id, WorkOrderId, ItemId, InspectedQty, PassedQty, FailedQty, FailReason?, InspectedAt`

Phase 1 只需要這些欄位撐起兩個查詢工具即可，不含採購簽核流程、進貨異動等完整交易邏輯。

### 6.4 設定與模型選擇

- API Key 走 ASP.NET Core 標準做法：本機用 `dotnet user-secrets`，正式環境用環境變數，**絕不寫死在程式或 appsettings.json 進版控**。
- Model 名稱、`max_tokens`、tool-use 迴圈上限（預設 5）都做成可設定值（`appsettings.json` 的 `AiAssistantOptions` 區塊），方便之後換模型或調參數不用改程式碼。
- 這種查詢型任務不需要最貴的模型，之後實際串接時再依可用機型挑一個 cost/延遲平衡點較好的即可，先不鎖死特定型號名稱。

### 6.5 Logging / 可觀測性

`ToolDispatcher` 每次呼叫都記錄結構化 log：工具名稱、輸入參數、耗時、成功/失敗。這條 log 很好用——可以直接展示「AI 助理每一步呼叫了什麼工具、拿到什麼資料」，證明整個過程是可追蹤、可稽核的，不是黑箱。

### 6.6 System Prompt 草稿

作為 Phase 2 開發時的起點（之後可再微調）：

> 你是一個製造業 ERP 系統的助理。你只能透過提供的工具查詢系統中的即時資料，禁止依據常識、記憶或推測回答任何庫存量、工單狀態、缺料量等數字——所有涉及數字或狀態的回答都必須先呼叫對應工具，且只能使用工具回傳的數值。若工具回傳 `is_error: true` 或查無資料，必須如實告知使用者查詢失敗，不可重複呼叫超過一次，也不可編造替代答案。若 `search_items` 找到多筆符合的料號，必須先列出選項請使用者確認，不可自行選擇其中一筆繼續查詢。若使用者的問題與本系統的料號、庫存、工單、採購、品管無關，請禮貌說明你只能協助處理與本系統相關的查詢。回答一律使用繁體中文，語氣簡潔專業。

### 6.7 測試策略補充

- `ToolDispatcher` 的路由測試改成直接 mock 各個 Application Service 介面（而不只是 mock `ILlmClient`），確保「工具名稱 → 正確的服務方法 → 正確的參數轉換」這條路徑本身有測試覆蓋，不依賴 LLM 行為。
- `AiAssistantService`（tool-use 迴圈本體）用可腳本化的假 `ILlmClient`，模擬「回 tool_use → 回 tool_use → 最後回純文字」與「一直回 tool_use 直到超過上限」兩種情境，驗證迴圈邏輯與上限保護有效。
- 架構測試（NetArchTest）維持第 2 節講的：Domain 不得參考 `Infrastructure.AI` 或任何 LLM SDK。
