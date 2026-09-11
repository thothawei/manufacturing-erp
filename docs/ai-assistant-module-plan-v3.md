# 製造業 ERP 系統 — AI 助理模組規劃（v3）

> **這一版的性質跟前兩版不同。** [v1](ai-assistant-module-plan-v1.md)／[v2](ai-assistant-module-plan-v2.md) 是動工前的規劃，v3 是在 Phase 1–3 已經實作完成後，
> 拿最新一版規劃（含先前未納入的第 6 節「補齊的規劃缺口」）跟**實際的程式碼**逐條對帳，
> 據此重新規劃剩下的工作。
>
> 因此本文件的重點不是重述設計，而是三件事：
> 1. 哪些已經做到（可驗證）
> 2. 哪些規劃寫了但實作沒做到，或實作刻意偏離（含原因）
> 3. 剩下該做什麼，以及當時待決的兩個決策點（現已拍板）

---

## 1. 現況：Phase 1–3 已完成

| 階段 | 狀態 | 產出 |
|---|---|---|
| Phase 1 | 完成 | 八個 Application 查詢／計算服務、Domain 實體、EF Core 資料層、展示種子資料 |
| Phase 2 | 完成 | `Infrastructure.AI`：`ILlmClient` 抽象、`AnthropicLlmClient`、`ToolCatalog`、`ToolDispatcher`、tool-use 迴圈、`POST /api/ai-assistant/ask` |
| Phase 3 | 完成 | 八個工具全數接上、架構測試、防幻覺測試 |
| Phase 4 | 完成（一項待金鑰） | 展示腳本與設計問答（`docs/demo-and-design-notes.md`） |
| Phase 4 之後 | 完成 | REST 錯誤處理、seeding 併發修復、CI、Scalar UI、授權與格式規範（見 3.4） |

測試共 166 個（Application 43／Architecture 7／Infrastructure 103／Api 13），全數通過。

> **註（2026-09-11）**：這個數字是 v3 寫作當時的快照。之後新增了第 9 個工具
> （文件語意檢索，見 [rag-module-plan-v1](rag-module-plan-v1.md)），現行數字以 `README.md` 為準。

建置與格式檢查都在 CI 上跑，警告視為錯誤。
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
| G5 | log 要含輸入參數與耗時（6.5） | 只記工具名稱與成功／失敗 | 想展示的「每一步呼叫了什麼、拿到什麼」還不完整 |
| G6 | API key 用 `dotnet user-secrets`（6.4） | 只支援環境變數與設定檔 | 本機開發容易誤把金鑰寫進 appsettings 進版控 |

### 2.3 實作刻意偏離規劃 —— 這些偏離我認為應該保留

| # | 規劃條目 | 實作 | 理由 |
|---|---|---|---|
| D1 | `AnthropicLlmClient` 用「純 HTTP 呼叫」 | 用官方 Anthropic C# SDK | 規劃的目的是「換供應商只改一個類別」，這由 `ILlmClient` 抽象達成，與底下用不用 SDK 無關。手刻 HTTP 去組 `tool_use`／`tool_result` 的 union 結構只是額外放棄型別安全 |
| D2 | 採購單用 `PurchaseOrder` + `PurchaseOrderLine` 單頭單身（6.3） | 單行 `PurchaseOrder`（一單一料號） | 工具只需要「查未結採購單狀態」。單頭單身會讓 MRP 的在途量計算多一層 join，卻不增加任何 AI 能回答的問題。見 3.2 的決策點 |
| D3 | `QualityInspectionRecord` 有 `FailedQty` 欄位 | `FailedQty` 是計算屬性（`InspectedQty - PassedQty`） | 存成欄位會出現「三個數字對不起來」的髒資料，計算屬性讓它不可能不一致 |
| D4 | `ToolDispatcher` 路由測試 mock 各 Application Service（6.7） | 用真 EF Core + 種子資料跑完整鏈路 | mock 版只能證明「呼叫了某個方法」，真資料版連數字對不對都一起驗證。此外另有一致性測試走訪 `ToolCatalog` 每個工具（含選填參數）真的執行一次 |
| D5 | 架構測試用 NetArchTest 檢查 Domain 不參考 LLM SDK | NetArchTest **加上**組件參考檢查 | 實測發現 NetArchTest 檢查的是 `Erp.*` 命名空間，對外部套件無感 —— 在 Domain 寫 `typeof(Anthropic.AnthropicClient)` 時只有組件參考那條會紅。光靠 NetArchTest 擋不住這件事 |

### 2.4 技術棧不一致

| 項目 | 規劃（第 7 節） | 實作 |
|---|---|---|
| 資料庫 | SQL Server | **SQLite** |

這是動工時的環境決定（開發機沒有 SQL Server），當時沒有回頭跟規劃對齊，是我的疏漏。

現在的實際影響 —— **這一段是修正後的內容，原本寫錯了**：

我原先在文件與程式碼註解中寫「SQLite 把 decimal 存成 TEXT，資料庫層無法正確比較或排序」，
並打算為此加一條約束測試。實測後發現**這個說法是錯的**：EF Core 的 SQLite provider 會在連線上
註冊 `ef_compare()`、`ef_sum()` 與 `EF_DECIMAL` collation，透過 EF 下的比較、排序、加總都正確。

```sql
WHERE ef_compare("i"."OnHandQty", '50.0') > 0
ORDER BY "i"."OnHandQty" COLLATE EF_DECIMAL
```

那條「約束」不存在，所以也不需要為它加保護 —— 加了就是在防一個不存在的問題。
改為加一條 `SqliteDecimalBehaviourTests` 把真實行為釘住，換 provider 或 EF 版本改變行為時會紅。

**真正的限制**是這些函式只存在於 EF Core 開的連線：用 `sqlite3` CLI、DB browser
或手寫原生 SQL 查同一個檔案時，TEXT 會退回字典序，`"9"` 會大於 `"100"`。
目前沒有任何原生 SQL，這條限制暫時不影響什麼，但要寫進文件以免日後有人加了原生查詢。

round-trip 保留小數位數（`30m` → `30.0`）的問題是真的，已由 `NormalizedDecimalConverter` 處理。

**這件事本身值得記錄**：文件裡一句沒實測就寫下的「常識」，差點導致一條保護不存在問題的測試。

決策點見 3.2。

---

## 3. 重新規劃：剩下要做什麼

### 3.1 Phase 3.5 — 補齊真缺口（建議優先做）

按重要性排序。G1 是唯一會造成使用者可見故障的一條。

| # | 工作 | 產出 | 估計 |
|---|---|---|---|
| ~~G1~~ | ~~`ToolDispatcher` 加上總括的例外攔截~~ **已完成** | 見下方 3.1.1 | — |
| ~~G2~~ | ~~錯誤回傳改為 `{ "error_code": ..., "message": ... }`~~ **已完成** | 見下方 3.1.2 | — |
| ~~G3+G4~~ | ~~system prompt 補兩條規則~~ **已完成** | 見下方 3.1.3 | — |
| ~~G5~~ | ~~log 補上輸入參數與耗時~~ **已完成** | 見下方 3.1.3 | — |
| ~~G6~~ | ~~支援 `dotnet user-secrets`~~ **已完成** | 見下方 3.1.3 | — |

**Phase 3.5 全部完成。**

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

### 3.1.3 G3、G4、G5、G6 已完成

**G3（超出範圍禮貌拒答）與 G4（失敗不重複重試）** 寫進 system prompt。
G4 的錯誤碼部分已在 G2 完成，這次補上「同一個工具用相同參數失敗過一次就不要再呼叫第二次，
重試前必須先改變參數」。

新增一組 system prompt 覆蓋測試。**它不驗證 LLM 是否真的遵守規則** —— 那需要真實 API 與行為評測；
它防的是有人重寫或精簡 prompt 時把某條規則整個刪掉而沒人發現。實測刪掉超出範圍那條會紅。

**G5（稽核軌跡）** 放在 `ToolDispatcher`（規劃 6.5 指定的位置），每次呼叫記錄工具名稱、
參數、耗時、成功與否。參數用與工具輸出同一組序列化設定，否則中文會被逃逸成 `\uXXXX`，
log 是給人看的那樣讀不出來查了什麼。五條測試涵蓋，包含「稽核紀錄不含金鑰或連線字串」。

**G6（user-secrets）** 加上 `UserSecretsId`。順帶在啟動時印出生效的設定與金鑰來源
（只印來源、不印值）—— 否則「user-secrets 到底有沒有被讀到」只能靠猜。
實測驗證：設定 `AiAssistant:TimeoutSeconds=45` 後啟動，log 顯示逾時從 60 變成 45，
金鑰來源從「未設定」變成「設定檔或 user-secrets」。

### 3.2 兩個決策（已拍板：都維持現狀）

**決策一：資料庫要不要遷到 SQL Server？→ 維持 SQLite**

| 選項 | 代價 | 得到什麼 |
|---|---|---|
| **維持 SQLite**（建議） | 保留「數量不得在 SQL 層比較排序」這條靠自律的約束 | 零遷移成本；`dotnet run` 就能跑起來，展示時不需要任何外部服務 |
| 遷到 SQL Server | 約 1 次工作階段：換 provider、重產 migration、`decimal(18,4)` 精度設定、跑 Docker 或 LocalDB | decimal 原生支援，約束消失；與原規劃的技術棧一致 |

理由是這個專案的價值在架構與 AI 模組，而「對方能在自己機器上一行指令跑起來」比技術棧書面一致更有說服力。

**原本承諾的配套已取消**：當時說「若維持 SQLite 就補一條測試把那條約束變成 CI 保護的規則」，
但動手前先實測，發現那條約束根本不存在（見 2.4）。改為 `SqliteDecimalBehaviourTests` 釘住真實行為 ——
為不存在的問題加保護測試等於製造假工作。

**決策二：採購單要不要改成單頭單身？→ 維持單行**

| 選項 | 代價 | 得到什麼 |
|---|---|---|
| **維持單行**（建議） | 與原規劃 6.3 不一致 | 現有工具功能不變 |
| 改單頭單身 | 約 0.5 次工作階段：拆實體、改 migration、改種子與測試 | 資料模型更貼近真實 ERP，被問「一張採購單能不能有多個料號」時答得更漂亮 |

理由是八個工具沒有一個需要它。若日後預期會被追問採購模組的資料模型設計，
改成單頭單身的成本約半次工作階段 —— 那確實是實務上的標準做法，
屬於「作品集要不要展示更完整的建模能力」的取捨，不是技術對錯。

### 3.3 Phase 4 — 展示準備（重新定義）

原規劃的 Phase 4 是「示範對話腳本、curl/Postman 範例、README 架構決策說明」。
README 目前已涵蓋架構決策與 curl 範例，所以 Phase 4 收斂成：

| 工作 | 狀態 |
|---|---|
| 關閉真實 API 驗證缺口 | **未完成 —— 需要 Anthropic API 金鑰**，開發機沒有 |
| 示範對話腳本 | 完成。六組 curl，輸出都是實際跑出來貼上的 |
| 設計問答準備 | 完成。八題，每題都指得出對應的程式碼或測試 |
| 可觀測性展示 | 完成（`demo-and-design-notes.md` 第 3.7 節） |

產出：`docs/demo-and-design-notes.md`。

**兩個決策已拍板：維持 SQLite、維持單行採購單。**
決策一原本承諾的配套（把 decimal 約束變成 CI 保護的測試）在實測後取消 —— 見 2.4 節，
那條約束不存在，改為 `SqliteDecimalBehaviourTests` 釘住真實行為。

### 3.4 Phase 4 之後的追加工作

規劃走完之後，又做了一輪「還有什麼需要加強」的檢查，找到三個真問題並修掉。
這一節記錄它們，因為前面的規劃文件不會涵蓋規劃之外發現的東西。

| 項目 | 起因 | 產出 |
|---|---|---|
| **REST 端點錯誤處理** | 實測發現查無料號、查無工單、對原物料問可製造量、規劃天數為 0 全部回 500，且回應體吐出完整堆疊與本機絕對路徑 | `ErpExceptionHandler` 對映 404／400／409／503；新增 `Erp.Api.Tests`（13 個） |
| **seeding 併發競態** | 一個 flaky 測試，症狀是「每個 build 組態的第一次執行才失敗」 | `ErpDbSeeder` 容忍併發衝突；`SeederConcurrencyTests`（3 個） |
| **沒有 CI** | 規劃第 2 節說架構測試要是「永遠會跑的 CI 測試」，但 repo 根本沒有 CI | GitHub Actions：格式檢查 → Release 建置（警告視為錯誤）→ 全部測試 |

另外補了三項展示與工程配套：

- **Scalar 互動式 API 文件**（`/scalar/v1`）：專案原本沒有任何可展示的畫面，
  端點只能用 curl 或看瀏覽器裡的原始 JSON。十個端點都有中文說明與參數型別，可直接試打。
- **MIT LICENSE**：公開 repo 沒有授權條款等於保留所有權利，別人不能合法使用。
- **`.editorconfig` + CI 格式檢查**：C# 4 空格（既有寫法與 .NET 慣例），
  沒有 CI 驗證的話 `.editorconfig` 只是建議而不是規範。

**seeding 那條值得單獨說**：第一次寫的併發測試顯示「併發 seeding 成功」，
差點就據此排除這個方向。那個測試用 in-memory SQLite —— 共用單一連線，寫入天然序列化，
根本測不出併發。改用檔案 SQLite 才重現。**測試環境與真實環境的差異本身就會製造偽陰性。**

---

## 4. 修訂後的工作順序

```
Phase 3.5  補齊真缺口 G1–G6                        已完成
   ↓
決策一／決策二                                      已拍板：都維持現狀
   ↓
Phase 4    展示腳本 → 設計問答                      已完成
   ↓
追加工作   REST 錯誤處理／seeding 併發／CI          已完成（見 3.4）
           Scalar UI／LICENSE／.editorconfig
```

**唯一未完成的仍是真實 API 驗證** —— 那不是工作量問題，是需要 Anthropic API credits。
Claude Pro 訂閱不涵蓋直接的 Messages API 呼叫（官方明列在排除範圍），需另外儲值。

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
