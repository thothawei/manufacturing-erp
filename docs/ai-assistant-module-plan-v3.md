# 製造業 ERP 系統 — AI 助理模組規劃（v3）

> **這一版的性質跟前兩版不同。** v1／v2 是動工前的規劃，v3 是在 Phase 1–3 已經實作完成後，
> 拿最新一版規劃（含先前未納入的第 6 節「補齊的規劃缺口」）跟**實際的程式碼**逐條對帳，
> 據此重新規劃剩下的工作。
>
> 因此本文件的重點不是重述設計，而是三件事：
> 1. 哪些已經做到（可驗證）
> 2. 哪些規劃寫了但實作沒做到，或實作刻意偏離（含原因）
> 3. 剩下該做什麼，以及需要你拍板的決策點

---

## 1. 現況：Phase 1–3 已完成

| 階段 | 狀態 | 產出 |
|---|---|---|
| Phase 1 | 完成 | 八個 Application 查詢／計算服務、Domain 實體、EF Core 資料層、展示種子資料 |
| Phase 2 | 完成 | `Infrastructure.AI`：`ILlmClient` 抽象、`AnthropicLlmClient`、`ToolCatalog`、`ToolDispatcher`、tool-use 迴圈、`POST /api/ai-assistant/ask` |
| Phase 3 | 完成 | 八個工具全數接上、架構測試、防幻覺測試 |
| Phase 4 | 未開始 | 展示準備 |

測試共 112 個（Application 43／Architecture 7／Infrastructure 62），全數通過。
分層邊界由 `Erp.ArchitectureTests` 保護：Domain 不得相依其他層、不得參考 EF Core 或任何 LLM 廠商套件。

**一個尚未關閉的驗證缺口**：`AnthropicLlmClient` 從未對真實 Anthropic API 發過請求（開發機沒有金鑰）。
送出的 HTTP 請求內容已用本機假伺服器逐欄檢查，但「Anthropic 伺服器會接受這個請求」尚未驗證。

---

## 2. 規劃與實作的逐條對帳

### 2.1 已符合規劃

| 規劃條目 | 實作 |
|---|---|
| Domain 完全不知道 AI 存在 | 架構測試保護，違反會讓 CI 紅 |
| `IAiAssistantService` 定義在 Application、實作在 Infrastructure | 是，與 Repository 同一套依賴反轉 |
| 八個工具全為唯讀查詢 | 是，沒有任何工具會寫入資料庫 |
| 不讓 LLM 自己組 SQL 或算數字 | 是，工具只轉呼叫既有服務，回傳後端算好的結構化 JSON |
| tool-use 迴圈上限 5 輪 | 是，可由設定調整，達上限回覆說明而非拋例外 |
| 單輪問答、不做跨請求對話記憶（6.1） | 是，API 合約與規劃一致 |
| `search_items` 多筆時請使用者確認（6.2） | 是，system prompt 第 3 條 |
| Model／`max_tokens`／迴圈上限可設定（6.4） | 是，`AiAssistantOptions` |
| 可腳本化的假 `ILlmClient` 測試迴圈與上限（6.7） | 是，`FakeLlmClient` |
| 架構測試（6.7） | 是，且比規劃更嚴：見 2.3 |

### 2.2 規劃寫了但實作沒做到 —— 這些是真缺口

| # | 規劃條目 | 實作現況 | 影響 |
|---|---|---|---|
| G1 | 未預期例外要攔下、記 log、回 LLM 通用 `INTERNAL_ERROR`（6.2） | `ToolDispatcher` 只攔三類已知例外，其餘往上拋 | **一個工具出錯會讓整個對話掛掉並回 500**，這是目前最該補的一條 |
| G2 | 錯誤回傳結構化 `error_code`（6.2） | 只回 `{"error":"訊息文字"}`，無錯誤碼 | LLM 無法穩定區分錯誤類型，只能靠讀中文訊息 |
| G3 | 超出範圍的問題要禮貌拒答（6.2） | system prompt 沒有這條 | 使用者問天氣時行為未定義 |
| G4 | system prompt「工具失敗不可重複呼叫超過一次」（6.6） | 沒有這條 | 工具失敗時 LLM 可能反覆重試直到用完 5 輪 |
| G5 | log 要含輸入參數與耗時（6.5） | 只記工具名稱與成功／失敗 | 面試時想展示的「每一步呼叫了什麼、拿到什麼」還不完整 |
| G6 | API key 用 `dotnet user-secrets`（6.4） | 只支援環境變數與設定檔 | 本機開發容易誤把金鑰寫進 appsettings 進版控 |

### 2.3 實作刻意偏離規劃 —— 這些偏離我認為應該保留

| # | 規劃條目 | 實作 | 理由 |
|---|---|---|---|
| D1 | `AnthropicLlmClient` 用「純 HTTP 呼叫」 | 用官方 Anthropic C# SDK | 規劃的目的是「換供應商只改一個類別」，這由 `ILlmClient` 抽象達成，與底下用不用 SDK 無關。手刻 HTTP 去組 `tool_use`／`tool_result` 的 union 結構只是額外放棄型別安全 |
| D2 | 採購單用 `PurchaseOrder` + `PurchaseOrderLine` 單頭單身（6.3） | 單行 `PurchaseOrder`（一單一料號） | 工具只需要「查未結採購單狀態」。單頭單身會讓 MRP 的在途量計算多一層 join，卻不增加任何 AI 能回答的問題。見 3.2 的決策點 |
| D3 | `QualityInspectionRecord` 有 `FailedQty` 欄位 | `FailedQty` 是計算屬性（`InspectedQty - PassedQty`） | 存成欄位會出現「三個數字對不起來」的髒資料，計算屬性讓它不可能不一致 |
| D4 | `ToolDispatcher` 路由測試 mock 各 Application Service（6.7） | 用真 EF Core + 種子資料跑完整鏈路 | mock 版只能證明「呼叫了某個方法」，真資料版連數字對不對都一起驗證。此外另有一致性測試走訪 `ToolCatalog` 每個工具（含選填參數）真的執行一次 |
| D5 | 架構測試用 NetArchTest 檢查 Domain 不參考 LLM SDK | NetArchTest **加上**組件參考檢查 | 實測發現 NetArchTest 檢查的是 `Erp.*` 命名空間，對外部套件無感 —— 在 Domain 寫 `typeof(Anthropic.AnthropicClient)` 時只有組件參考那條會紅。光靠 NetArchTest 擋不住這件事 |

### 2.4 技術棧不一致 —— 需要你決定

| 項目 | 規劃（第 7 節） | 實作 |
|---|---|---|
| 資料庫 | SQL Server | **SQLite** |

這是動工時的環境決定（開發機沒有 SQL Server），當時沒有回頭跟規劃對齊，是我的疏漏。

現在的實際影響：**SQLite 把 `decimal` 存成 TEXT，資料庫層無法正確比較或排序數量欄位。**
目前所有 Repository 都遵守「數量的比較、加總、排序一律在載入記憶體後才做」，
查詢條件只用字串、日期與列舉。這條約束寫在 `ErpDbContext` 的註解裡，但它是靠自律維持的，
沒有測試保護 —— 有人寫一個 `.Where(b => b.AvailableQty > 0)` 就會得到錯誤結果而不報錯。

另外 SQLite 的 round-trip 會讓 `30m` 變成 `30.0`，已由 `NormalizedDecimalConverter` 處理。

決策點見 3.2。

---

## 3. 重新規劃：剩下要做什麼

### 3.1 Phase 3.5 — 補齊真缺口（建議優先做）

按重要性排序。G1 是唯一會造成使用者可見故障的一條。

| # | 工作 | 產出 | 估計 |
|---|---|---|---|
| ~~G1~~ | ~~`ToolDispatcher` 加上總括的例外攔截~~ **已完成** | 見下方 3.1.1 | — |
| ~~G2~~ | ~~錯誤回傳改為 `{ "error_code": ..., "message": ... }`~~ **已完成** | 見下方 3.1.2 | — |
| G3+G4 | system prompt 補兩條：超出範圍禮貌拒答、同一工具失敗不重試超過一次 | 更新 `AiSystemPrompt` | 極小 |
| G5 | log 補上輸入參數與耗時，改成結構化欄位 | `AiAssistantService` 的 log 呼叫 + `Stopwatch` | 小 |
| G6 | 支援 `dotnet user-secrets`，README 補本機設定步驟 | `Program.cs` 一行 + 文件 | 極小 |

剩餘（G3–G6）合計約 0.5 次工作階段。

### 3.1.1 G1 已完成

`ToolDispatcher` 現在有總括的例外攔截：未預期例外降級成單一工具的失敗，
例外全文只進伺服器 log，回給 LLM 的訊息不含型別、堆疊或任何內部細節。
`OperationCanceledException` 在呼叫端主動取消時原樣往上拋 —— 那代表「這次對話不用做了」，
不該被降級成一個 tool_result 讓迴圈繼續跑。

六條測試涵蓋，用的是真實的資料庫連線失效（關掉 in-memory SQLite 連線）而不是 mock：

- 資料庫失效時回報錯誤而不是把例外往上拋
- 回給 LLM 的訊息不含例外型別、堆疊或內部細節
- 例外全文（含參數）寫進伺服器 log
- 八個工具全部走一遍，確認每一個都被攔下
- 呼叫端主動取消時例外往上拋
- **一個工具炸掉時，同一輪其他工具的結果仍然送達 LLM** —— 這是 G1 真正的價值

反向驗證：移除總括攔截會紅 4 條，只拿掉取消的 filter 會紅 1 條。

順帶修掉一個同源問題：log 裡的參數原本用 `JsonElement.ToString()` 輸出，
會把中文逃逸成 `\uXXXX`。log 是給人看的，那樣根本讀不出來查了什麼，改用同一組序列化設定。

### 3.1.2 G2 已完成

五個錯誤碼，wire 值寫死在 `ToolErrorCodeExtensions` —— 錯誤碼是對外契約的一部分，
改 enum 成員名稱不該意外改掉送給 LLM 的值。

| error_code | 對應例外／情境 |
|---|---|
| `ENTITY_NOT_FOUND` | `EntityNotFoundException`（料件、工單都適用） |
| `INVALID_ARGUMENT` | `ArgumentException`：參數缺漏、日期或數字格式錯 |
| `NOT_APPLICABLE` | `InvalidOperationException`：參數合法但問法不適用 |
| `UNKNOWN_TOOL` | 呼叫了不存在的工具 |
| `INTERNAL_ERROR` | 未預期例外（G1） |

**與規劃 6.2 的一處微調**：規劃寫 `ITEM_NOT_FOUND`，實作用 `ENTITY_NOT_FOUND`。
查無資料不只發生在料件（工單也會），用通用碼比較正確，是什麼查不到由 `message` 說明。

system prompt 補上逐碼的反應規則 —— 只給錯誤碼而不說明該怎麼反應的話，
LLM 還是只能靠讀中文訊息猜，等於白做。這條同時涵蓋了 G4（工具失敗不重複重試）的一半：
`ENTITY_NOT_FOUND`、`NOT_APPLICABLE`、`INTERNAL_ERROR` 都明寫「不要重試」。

九條契約測試涵蓋每個錯誤碼，外加一條完整性測試：新增 enum 成員卻忘記加 wire 字串會直接紅。
反向驗證：忘記加字串會抓到、誤分類紅 3 條、移除 `error_code` 欄位紅 13 條。

順帶清掉兩個先前被 `-v q` 隱藏的可空性警告，現在建置是 0 警告。

### 3.2 需要你拍板的兩個決策

**決策一：資料庫要不要遷到 SQL Server？**

| 選項 | 代價 | 得到什麼 |
|---|---|---|
| **維持 SQLite**（建議） | 保留「數量不得在 SQL 層比較排序」這條靠自律的約束 | 零遷移成本；`dotnet run` 就能跑起來，面試 demo 不需要任何外部服務 |
| 遷到 SQL Server | 約 1 次工作階段：換 provider、重產 migration、`decimal(18,4)` 精度設定、跑 Docker 或 LocalDB | decimal 原生支援，約束消失；與原規劃的技術棧一致 |

我建議**維持 SQLite**，理由是這個專案的價值在架構與 AI 模組，而「面試時對方能在自己機器上一行指令跑起來」比技術棧書面一致更有說服力。
如果選擇維持，我會補一條測試把那條約束變成 CI 保護的規則（掃描 Repository 的 LINQ 不得對 decimal 屬性做比較），而不是繼續靠註解。

如果你是為了讓履歷上寫得出 SQL Server，那就遷 —— 這是合理的理由，說一聲我就做。

**決策二：採購單要不要改成單頭單身？**

| 選項 | 代價 | 得到什麼 |
|---|---|---|
| **維持單行**（建議） | 與原規劃 6.3 不一致 | 現有工具功能不變 |
| 改單頭單身 | 約 0.5 次工作階段：拆實體、改 migration、改種子與測試 | 資料模型更貼近真實 ERP，面試被問「一張採購單能不能有多個料號」時答得更漂亮 |

我建議**維持單行**，因為八個工具沒有一個需要它。但如果你預期面試會被追問採購模組的資料模型設計，
改成單頭單身的成本不高，而且那確實是實務上的標準做法 —— 這是「作品集要不要展示更完整的建模能力」的取捨，不是技術對錯。

### 3.3 Phase 4 — 展示準備（重新定義）

原規劃的 Phase 4 是「示範對話腳本、curl/Postman 範例、README 架構決策說明」。
README 目前已涵蓋架構決策與 curl 範例，所以 Phase 4 收斂成：

| 工作 | 說明 |
|---|---|
| **關閉真實 API 驗證缺口**（優先） | 帶真金鑰跑一輪完整問答，確認 Anthropic 接受我們送的請求格式。這是目前唯一「編譯過、測試過、但沒真的跑過」的環節 |
| 示範對話腳本 | 3–4 組問答，涵蓋：單一工具查詢、多階 BOM 可製造量、風險工單＋採購建議（兩個工具接力）、查無資料時的誠實回覆 |
| 面試問答準備 | 針對「為什麼 Domain 不碰 AI」「為什麼工具都唯讀」「怎麼防止 AI 編數字」「為什麼用 SQLite」各準備一段可講的答案，每段都要指得出對應的程式碼或測試 |
| 可觀測性展示 | 依賴 G5 完成。跑一次問答，把 log 貼出來展示「AI 每一步呼叫了什麼工具、參數是什麼、花了多久」 |

估計 1–2 次工作階段。

---

## 4. 修訂後的工作順序

```
Phase 3.5  補齊真缺口 G1–G6                        約 1 次
   ↓
決策一／決策二（你拍板）                            視選擇 0–1.5 次
   ↓
Phase 4    真實 API 驗證 → 展示腳本 → 面試問答      約 1–2 次
```

若兩個決策都採用建議（維持現狀），剩餘總計約 **2–3 次工作階段**。

---

## 5. 與 v2 的關係

v2 提出的四項修訂全部已實作並有測試保護：

- 庫存計算基準統一為 `available_qty`，回傳帶 `basis` 欄位
- `required_per_finished_unit` 的分母是最終成品，多階用量逐層累乘
- `ToolCatalog` 與實作的一致性測試（v3 補充：連選填參數都會真的帶進去跑一次）
- 單輪對話列為已知限制

v2 未預見、實作時才發現的問題，都記在 `README.md` 的「三個實作上踩到的點」與「尚未處理」兩節，
包含 MRP 漏算逾期工單、EF Core 無法翻譯計算屬性、SDK 逃逸中文造成 2.28 倍請求體積等。
那些屬於實作紀錄，不重複寫進規劃。
