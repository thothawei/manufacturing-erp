# 展示腳本與設計問答

> 本文件的所有 REST 輸出都是實際跑出來貼上的，不是手寫的示意。
> AI 助理端點的**工具呼叫序列與數字**由測試驗證，但**回覆的自然語言文字**
> 尚未對真實 Anthropic API 驗證過（開發機沒有金鑰），文中會標明。

### 被問到細節時去哪查

| 對方問什麼 | 去哪 |
|---|---|
| 「這個工具的參數與回傳長怎樣」「為什麼要有 basis 欄位」 | [v2](ai-assistant-module-plan-v2.md) 第 2–4 節：工具契約、庫存計算基準、防幻覺機制。程式碼有五處註解指向這裡 |
| 「規劃跟做出來的東西差多少」「為什麼沒照原規劃做」 | [v3](ai-assistant-module-plan-v3.md) 第 2 節：逐條對帳，含五項刻意偏離的理由 |
| 「一開始是怎麼想的」 | [v1](ai-assistant-module-plan-v1.md)：動工前的原始規劃，保持原貌 |
| 「RAG 為什麼這樣選」「七個決策點的取捨」「實作與規劃差在哪」 | [rag-module-plan-v1](rag-module-plan-v1.md)，第 11 節是完成後的對帳 |
| 「架構決策」「踩過什麼坑」「測試策略」 | 專案根目錄的 `README.md` |

規劃演進是 v1 → v2 → v3：**v1 是原始構想、v2 是動工前的修訂、v3 是實作完成後的對帳**。
被追問「你怎麼確認做出來的東西跟規劃一致」時，v3 第 2 節就是答案。

---

## 1. 三十秒把它跑起來

```bash
export DOTNET_ROOT="/opt/homebrew/opt/dotnet/libexec"   # Homebrew 安裝 .NET 時需要
dotnet run --project src/Erp.Api --urls http://localhost:5199
```

不需要 Docker、不需要外部資料庫。開發模式會自動建表並灌入展示資料，
資料庫是一個 SQLite 檔（`src/Erp.Api/erp.db`），刪掉再啟動就重新產生。

**展示時從這裡開始**：http://localhost:5199/scalar/v1

![Scalar API 文件首頁](images/scalar-overview.png)

互動式 API 文件（Scalar）。左側是十六個端點的中文清單，主頁說明了這個系統在做什麼，
以及兩個最容易答錯的地方 —— 可用庫存與帳上庫存的差別、多階 BOM 用量的分母。

點進任一端點，會看到中文說明、參數型別、curl 範例，以及一個可以直接試打的 Test Request：

![AI 助理端點的詳細說明](images/scalar-ai-endpoint.png)

端點說明刻意不只寫「這個端點做什麼」，也寫清楚使用上的陷阱 ——
例如 AI 助理這條寫明「一次請求內部會有多輪 LLM 與工具的往返」，並說清楚
`conversationId` 的生命週期（記憶體、最近 6 輪、閒置 60 分鐘），因為那正是最常被誤解的地方。

下面第 3 節的 curl 都可以改用這個介面操作，展示時對方不必看終端機。

啟動時會印出 AI 助理的生效設定：

```
AI 助理設定：模型 anthropic/claude-opus-5，端點 http://localhost:20128（server-side refusal fallback 關閉），工具迴圈上限 5 輪，逾時 60 秒，API 金鑰來源：未設定（將交由 SDK 自行解析憑證，若無憑證會回 503）
```

端點那一段是預設走本機 OmniRoute gateway 的結果（見 README「AI 助理」）；
沒設 `BaseUrl` 時會印「Anthropic 官方」。要用 AI 助理才需要金鑰：

```bash
dotnet user-secrets set "AiAssistant:ApiKey" "sk-..." --project src/Erp.Api
```

金鑰存在專案外（`~/.microsoft/usersecrets/`），不可能被誤 commit —— 這個 repo 是公開的。

**文件語意檢索（RAG 那個工具）需要本機 Ollama，但它是可選的。** 沒裝的話啟動時印：

```
文件語意檢索：索引 0 段，模型 bge-m3，相似度門檻 0.5（索引未建立：需要本機 Ollama 並執行 ollama pull bge-m3；其餘工具不受影響）
```

服務照常啟動、核心端點全部正常，只有 `search_documents` 會回 `SERVICE_UNAVAILABLE`。
這條路徑是實跑驗證過的（`/health` 回 200），並由 `RagOptionalModuleTests` 把關 ——
「評審 clone 下來沒裝 Ollama」是最可能發生的情境，那時對方該看到一個少了可選功能的專案，
而不是一個跑不起來的專案。

要啟用：`brew install ollama && ollama serve`、`ollama pull bge-m3`，
然後刪掉 `erp.db` 重新啟動，索引會在灌種子資料之後自動建立（實測 1969 ms）：

```
文件檢索索引建立完成：7 份文件、33 段、模型 bge-m3
文件語意檢索：索引 33 段，模型 bge-m3，相似度門檻 0.5
```

### 開發時的三個指令

```bash
dotnet test                          # 397 個測試（本機有 Ollama 時 427）
dotnet format --verify-no-changes    # 格式是否符合 .editorconfig
dotnet build -warnaserror            # 警告視為錯誤，與 CI 一致
```

三者都由 GitHub Actions 在每次 push 與 PR 上執行（Release 組態），
另有一個 `retrieval-quality` job 裝 Ollama 跑那 30 個檢索品質測試。
格式規範是 C# 4 空格、專案檔與 JSON/YAML 2 空格、統一 LF 換行；
Markdown 不砍行尾空白（那是換行語法），EF 產生的 migration 標記為 generated code 不套用規範。

**加上格式檢查的理由**：沒有 CI 驗證的話，`.editorconfig` 只是一份建議而不是規範。

---

## 2. 展示資料的結構

```
TV-100（液晶電視）
├── PANEL-01   × 2      面板：帳上 100 片，其中 20 片已被其他工單保留 → 可用 80 片
├── CHASSIS-02 × 3
│   └── SCREW-05 × 4    螺絲對成品的用量是 3 × 4 = 12 支
└── CABLE-07   × 1
```

兩張未結案工單：一張缺料、一張逾期。**所有日期與單號都以執行當天為基準相對產生**，
資料不會過期 —— 下面範例輸出裡的 `2026-09-10` 會換成你執行當天的日期。

---

## 3. 展示腳本

### 3.1 可用庫存 vs 帳上庫存（一個容易答錯的問題）

```bash
curl "http://localhost:5199/api/items/TV-100/sufficiency"
```

```json
{"itemCode":"TV-100","bomVersion":"v3","basis":"available","maxBuildableQty":40,
 "sufficientForRequestedQty":null,"shortageComponents":[]}
```

**看點**：面板帳上有 100 片，除以每台 2 片會得到 50 台。正確答案是 **40 台** ——
因為 20 片已被其他工單保留。`basis: "available"` 就是在宣告這件事。
用帳上庫存計算會系統性偏樂觀，實務上就是「系統說夠、現場缺料」。

### 3.2 指定產量時列出瓶頸

```bash
curl "http://localhost:5199/api/items/TV-100/sufficiency?plannedQty=100"
```

```json
{"itemCode":"TV-100","bomVersion":"v3","basis":"available","maxBuildableQty":40,
 "sufficientForRequestedQty":false,
 "shortageComponents":[{"componentCode":"PANEL-01","componentName":"面板",
   "requiredPerFinishedUnit":2,"availableQty":80,"shortfallQty":120}]}
```

**看點**：`requiredPerFinishedUnit` 的分母是「最終成品一台」而不是直接上階父件。
螺絲在這個結構下是 12 而不是 4（3 × 4），多階 BOM 的累乘由後端處理，不讓 LLM 自己換算。

### 3.3 風險工單：兩種風險來源

```bash
curl "http://localhost:5199/api/work-orders/at-risk"
```

```json
[{"workOrderNo":"WO-20260910-02","itemCode":"MON-200","dueDate":"2026-09-08",
  "delayDays":2,"riskReason":"已逾交期 2 天，尚有 10 個未完工"},
 {"workOrderNo":"WO-20260910-01","itemCode":"TV-100","dueDate":"2026-09-13",
  "delayDays":2,"riskReason":"缺料：PANEL-01 短少 120 件"}]
```

**看點**：兩張都延遲 2 天，但原因完全不同 —— 一張已經逾期，一張是補料前置期（5 天）
超過剩餘工作天（3 天）。缺料判定只針對「剩餘待產數量」，已完工的部分不會重複算料。

不給區間時查到本週日為止，**但已逾交期未結案的舊工單一律納入** ——
逾期是兩種風險來源中最急的那種，不該因為交期落在查詢區間之前就看不到。
想看更長的期間就給天數（AI 助理聽到「未來 14 天」時傳的也是這個參數）：

```bash
curl "http://localhost:5199/api/work-orders/at-risk?windowDays=14"
```

### 3.4 MRP：建議採購量不是缺料量

![MRP 端點在 Scalar 中的說明](images/scalar-mrp-endpoint.png)

API 文件裡就寫明了兩個關鍵前提：已逾期未結案的工單也會納入、
`suggestedOrderQty` 已套用最小訂購量與訂購倍量所以要直接引用。
這兩件事都是用了才會發現的陷阱，寫在端點說明比藏在 README 有用。

```bash
curl "http://localhost:5199/api/mrp/shortages"
```

```json
{"basis":"available","horizonStart":"2026-09-10","horizonEnd":"2026-10-10",
 "shortageItems":[{"itemCode":"PANEL-01","itemName":"面板","grossRequirementQty":200,
   "availableQty":80,"inTransitQty":0,"netShortageQty":120,"neededByDate":"2026-09-13",
   "suggestedOrderQty":150,"supplierCode":"SUP-008","leadTimeDays":5}]}
```

**三個看點**：

- `grossRequirementQty` 是 200 —— 只有 WO-01 那 100 台 TV 的面板需求。
  **已逾期未結案的工單一樣要料**（查詢起點不是「今天」，這是實作時測試抓到的一個 bug），
  但這裡那張逾期的 MON-200 工單已經「已全數發料」：料早就出庫、帳上庫存也扣過了，
  再把它的剩餘產量算成需求就是同一份料算兩次。曾經有一版是這樣，缺料量被灌水成 130。
- `inTransitQty` 是 0，但其實有一張 30 片的採購單在途 —— 它 10 天後才到，
  趕不上 9/13 的需求日，所以不能算成供給。
- `suggestedOrderQty` 是 150 而不是 120 —— 套用了訂購倍量 50。
  這個數字要直接引用，system prompt 明文禁止 LLM 自己從缺料量推算。

### 3.4b MRP 時間分期：什麼時候開始缺，不是缺多少

```bash
curl "http://localhost:5199/api/mrp/time-phased?itemCode=PANEL-01"
```

```json
{"basis":"available","horizonStart":"2026-09-10","horizonEnd":"2026-11-04","weekCount":8,
 "items":[{"itemCode":"PANEL-01","itemName":"面板","openingAvailableQty":80,
   "buckets":[{"weekIndex":1,"weekStart":"2026-09-10","weekEnd":"2026-09-16",
     "scheduledReceiptQty":0,"requirementQty":200,"projectedOnHandQty":-120},
    {"weekIndex":2,"weekStart":"2026-09-17","weekEnd":"2026-09-23",
     "scheduledReceiptQty":30,"requirementQty":0,"projectedOnHandQty":-90}],
   "firstShortageWeek":1,"firstShortageDate":"2026-09-10"}]}
```

（上面只節錄前兩桶，實際回八桶。）

**看點**：上一節的 `/api/mrp/shortages` 只回答「總共缺 120 片」，
這裡回答的是「第 1 週就見底、第 2 週那 30 片到了也只補到 -90」。
需求分散在不同交期時這兩個答案可以差很多 —— 總量看起來夠，卻在第三週先見底，
後面的入庫再多也來不及。這正是不分期的版本結構上看不出來的事。

刻意**不做** lot-sizing（EOQ、Wagner-Whitin 那類批量最佳化）：這裡要展示的是
「時間分期怎麼用資料結構表達」，不是重寫一套供應鏈最佳化引擎。建議採購量仍由
`/api/mrp/shortages` 那條路徑的最小訂購量／訂購倍量規則產生，兩個端點各司其職。

桶以「今天起算每 7 天」切，不對齊日曆週 —— 對齊的話第一桶會是長度不定的殘週，
「第一週就缺料」這種結論會隨著今天是星期幾而改變。

### 3.5 品管：後端先彙總，不讓 LLM 加總

工單號是以執行當天產生的（`WO-<今天>-02`），所以先帶入日期：

```bash
curl "http://localhost:5199/api/quality/summary?workOrderNo=WO-$(date +%Y%m%d)-02"
```

```json
[{"workOrderNo":"WO-20260910-02","itemCode":"MON-200","inspectedQty":20,
  "passedQty":17,"failedQty":3,"failReasonSummary":"外觀刮傷、亮點超標"}]
```

**看點**：這張工單有兩筆檢驗紀錄，後端合併成一列才交給 LLM。
凡是「多筆資料要加總」的情況都在後端做完，減少 LLM 動手算的機會。

### 3.6 AI 助理

```bash
curl -X POST http://localhost:5199/api/ai-assistant/ask \
  -H 'Content-Type: application/json' \
  -d '{"question":"這週有哪些工單有延遲風險？缺料的話幫我列建議採購清單。"}'
```

一次 HTTP 請求內部會跑多輪 LLM ↔ 工具往返：

1. LLM 要求呼叫 `list_work_orders_at_risk()`
2. 後端回傳上面 3.3 的 JSON
3. LLM 要求呼叫 `run_mrp_shortage_analysis()`
4. 後端回傳上面 3.4 的 JSON
5. LLM 組出自然語言答案

**步驟 1–4 與其中的數字由 `AiAssistantScenarioTests` 驗證**（用假 LLM 跑真資料庫）。
步驟 5 的文字尚未對真實 API 驗證過。

沒有設定金鑰時回 503 與清楚訊息，不會洩漏 SDK 堆疊：

```json
{"title":"AI 助理暫時無法使用","status":503,"detail":"AI 助理尚未設定 API 金鑰，或金鑰無效"}
```

### 3.6b 追問：帶著 conversationId 就接得起來

```bash
# 第一問，回應會帶 conversationId
curl -X POST http://localhost:5199/api/ai-assistant/ask -H 'Content-Type: application/json' \
  -d '{"question":"面板還有多少可以用？"}'

# 追問，帶上剛剛拿到的識別碼
curl -X POST http://localhost:5199/api/ai-assistant/ask -H 'Content-Type: application/json' \
  -d '{"question":"那 CABLE-07 呢？","conversationId":"..."}'
```

第二次請求送進 LLM 的訊息是「舊問 → 舊答 → 新問」三則純文字，
**沒有**上一輪的工具呼叫與工具結果。理由與代價寫在 README 的「對話記憶」那一節。

識別碼由伺服器產生。放行呼叫端自己挑字串的話，猜一個別人用過的 id
就能讀到別人的對話歷史 —— 不是 GUID 一律回 400，這條有測試把關。

**驗證範圍要說清楚**：`ConversationMemoryTests` 驗的是「歷史有沒有被正確組進下一次請求」，
不是「LLM 有沒有因此聽懂追問」。後者需要真實模型與行為評測，用假 LLM 去斷言它聽懂了，
只會測到自己寫的腳本。

### 3.6c 可寫入工具：AI 提建議，人按核准

```bash
# AI 端：產生 PendingApproval 的建議，沒有採購單成立
curl -X POST http://localhost:5199/api/ai-assistant/ask -H 'Content-Type: application/json' \
  -d '{"question":"缺料的部分幫我開採購建議"}'

# 人工端：核准才會產生正式採購單
curl "http://localhost:5199/api/purchase-suggestions?status=PendingApproval"
curl -X POST http://localhost:5199/api/purchase-suggestions/PS-20260913-001/approve \
  -H 'Content-Type: application/json' -d '{"decidedBy":"王採購"}'
```

**看點**：這是整個系統裡唯一由 AI 寫入的東西，而它刻意不是採購單。
理由不是籠統的「怕 LLM 出錯」—— 採購會產生對外的金錢承諾，
而 LLM 的輸入（使用者的一句話、檢索到的文件內容）都是它控制不了的。
分開之後，最壞的結果就只是多一筆要被駁回的建議。

分界線落在程式碼的哪裡講得出來：`SuggestFromShortagesAsync` 是 AI 唯一通得到的入口，
只寫得出 PendingApproval；`ApproveAsync` 是唯一會新增採購單的地方，
只有 HTTP 端點呼叫得到，工具目錄裡沒有任何東西通得到它 —— 這條有測試釘住。

### 3.7 稽核軌跡

每次工具呼叫都留一筆結構化紀錄：

```
工具呼叫 get_item_inventory_status 完成，成功：True，耗時 12 ms，參數：{"item_code":"PANEL-01"}
```

助理最後只吐出一段自然語言，沒有這條 log 就無從得知那段話是根據哪些查詢組出來的。
這是「整個過程可追蹤、不是黑箱」的依據。

---

### 3.8 文件語意檢索：回段落與來源，不回「答案」

```bash
curl -X POST http://localhost:5199/api/ai-assistant/ask \
  -H 'Content-Type: application/json' \
  -d '{"question":"面板色偏的判定標準是什麼？以前有發生過批量客訴嗎？"}'
```

LLM 會呼叫 `search_documents(query="面板色偏的判定標準是什麼", top_k=2)`，後端回的是這個
（`text` 欄位為了版面截短了，實際回傳的是完整段落）：

```json
{
  "chunks": [
    {
      "text": "適用範圍：TV-100 與 MON-200 系列在面板點燈檢驗站（IPQC-02）發現色偏時的判定與處置。\n色偏定義為……（略）",
      "source_name": "品管異常處理 SOP — 面板色偏",
      "chunk_index": 0,
      "similarity": 0.7672788973169793
    },
    {
      "text": "判定方式：將待判面板置入標準光源箱，色溫固定 6500K，環境照度控制在 50 lux 以下。\n以色彩分析儀量測……（略）",
      "source_name": "品管異常處理 SOP — 面板色偏",
      "chunk_index": 1,
      "similarity": 0.7645242613616358
    }
  ],
  "similarity_threshold": 0.5,
  "matched_count": 2,
  "note": null
}
```

以上是對真實 Ollama（`bge-m3`）實跑貼上的。問「判定標準」回的正是 SOP 的
「適用範圍」與「判定方式」兩段。

**看點一**：工具回的是**段落與引用座標**，不是一個組好的答案。LLM 只負責把這兩段
組織成中文並標出來源，不做二次的相關性判斷 —— 那個判斷已經由後端的相似度門檻做完了。

**看點二**：`similarity` 低於 `similarity_threshold` 的片段**完全不會出現在這個 JSON 裡**。
讓 LLM 自己看分數決定「這段算不算相關」，等於把防幻覺的判準交給無法測試的一方。

**看點三**：`chunks` 為空時回的是**成功的空結果**加一句 `note`，不是錯誤 ——
查無資料是正常結果。問「今天天氣如何」時實測回的就是這個（33 段全部低於門檻）：

```json
{
  "chunks": [],
  "similarity_threshold": 0.5,
  "matched_count": 0,
  "note": "沒有找到相似度達到門檻的段落，語料中可能沒有這個主題。"
}
```

但「索引根本沒建起來」是另一回事，那回的是錯誤：

```json
{
  "error_code": "SERVICE_UNAVAILABLE",
  "message": "無法連線到 Ollama（http://localhost:11434）。文件語意檢索需要本機的 Ollama 服務，請確認它已啟動。"
}
```

把這兩種混為一談，使用者會以為文件裡真的沒寫那件事。

檢索層另外留一筆 log，通用那行記不到它獨有的兩個數字：

```
文件檢索完成，片段總數 33，命中 2 段，最高相似度 0.7673，門檻 0.5，耗時 80 ms
```

**最高相似度是過濾之前的，而且刻意不回給 LLM**。助理說「文件裡查不到」之後，
要判斷是語料真的沒有、還是門檻調太高，只有這個數字能回答。

---

### 3.9 錯誤處理：狀態碼要說對事情

```bash
curl "http://localhost:5199/api/items/NOT-EXIST/inventory"      # 料號不存在
curl "http://localhost:5199/api/items/PANEL-01/sufficiency"     # 料號存在但沒有 BOM
curl "http://localhost:5199/api/mrp/shortages?planningHorizonDays=0"
```

```json
{"title": "查無資料", "status": 404,
 "detail": "找不到料件：NOT-EXIST"}

{"title": "無法執行此操作", "status": 409,
 "detail": "料件 PANEL-01 沒有 BOM，無法計算可製造量"}

{"title": "參數錯誤", "status": 400,
 "detail": "規劃期間必須大於 0 天 (Parameter 'planningHorizonDays')"}
```

**看點**：第二個是 409 而不是 404 —— PANEL-01 **存在**，只是它是原物料沒有 BOM，
拿去問可製造量本來就不適用。這跟「查無此料號」是不同的情況，回傳的狀態碼也該不同。

這一層原本不存在：三個請求全部回 500，而且回應體直接吐出完整堆疊與本機絕對路徑。
現在未預期的錯誤只回通用訊息，全文進伺服器 log。

---

## 4. 設計問答

每一題的答案都指得出對應的程式碼或測試 —— 講不出證據的答案不要用。

### Q：為什麼 Domain 不能碰 AI？怎麼保證？

Domain 是 BOM、工單、庫存這些核心規則，它們的正確性跟用不用 LLM 無關。
LLM 整套換掉、甚至拿掉 AI 助理，Domain 都不該動一行。

保證的方式不是自律，是 `Erp.ArchitectureTests`：Domain 不得相依其他層、
不得參考 EF Core 或任何 LLM 廠商套件。違反會讓 CI 紅。

**這裡有個實作時的發現值得講**：一開始只用 NetArchTest，實測發現它擋不住 ——
它檢查的是 `Erp.*` 命名空間，對外部套件無感。在 Domain 裡寫
`typeof(Anthropic.AnthropicClient)` 時，只有我另外加的組件參考檢查會紅。
（順帶一提，用 `nameof` 測不出來，那是編譯期常數，不會在 IL 留下型別參考。）

### Q：怎麼防止 AI 編造數字？

四層，由強到弱：

1. **工具幾乎只能查。** 十二個工具裡十一個是唯讀的；唯一會寫入的
   `suggest_purchase_order` 寫出來的是待人工確認的建議，碰不到正式採購單。
   把「AI 能做的事」與「成立對外承諾的事」分開，是設計上的硬限制。
2. **數字都由後端算好。** 工具回傳固定 schema 的 JSON，LLM 拿到的是計算結果不是原始資料。
   連「兩筆檢驗紀錄加總」這種小事都在後端做完。
3. **system prompt 明文禁止推算**，並規定工具失敗時如實回報。
4. **測試。** `AiAssistantScenarioTests` 驗證送進 LLM 的每個數字都是後端算出來的值。

第 1 到 3 層都可能被繞過或失效，所以第 4 層才是真正的保證。

### Q：tool-use 迴圈會不會失控？

有輪數上限（預設 5，可設定），達到上限回覆一段說明而不是拋例外 ——
使用者需要知道發生什麼事，而不是看到 500。有測試模擬「LLM 每輪都要求呼叫工具、
永不給答案」，驗證會在第 N 輪停下。

### Q：一個工具壞掉會怎樣？

會降級成那一個工具的失敗，其他工具的結果照常送達 LLM。

這件事一開始沒做到 —— 原本只攔三類已知例外，資料庫連線失效會穿過整個迴圈變成 HTTP 500，
即使同一輪其他工具的結果是好的也一起陣亡。後來補上總括攔截，例外全文只進伺服器 log，
回給 LLM 的內容不含型別或堆疊（那些它可能原樣轉述給使用者）。

錯誤帶結構化 `error_code`，system prompt 逐碼規定該怎麼反應 ——
`ENTITY_NOT_FOUND` 直接告知查不到，`INVALID_ARGUMENT` 可改參數重試一次，其餘不要重試。

**REST 端點那邊犯過同樣的錯，而且更久才發現。** AI 路徑做了兩輪錯誤處理，
REST 端點卻完全裸奔 —— 查無料號回 500 並吐出堆疊。現在由 `ErpExceptionHandler`
統一對映（見 3.8），AI 端點原本自己 try/catch 的 503 也收斂進來，
避免兩套錯誤處理各說各話。

### Q：RAG 為什麼不用向量資料庫？幾萬份文件怎麼辦？

因為這個語料是 **33 個片段**。768 維 × 33 段約兩萬五千次乘加，比一次 HTTP 往返
便宜好幾個數量級 —— 查詢的真正瓶頸是 embedding 那一次網路呼叫，不是相似度計算。
在這個量級引入 Qdrant 或 pgvector，換來的只有「展示時要先啟動另一個服務」。

幾萬份文件的答案是換 ANN 索引，而要換的是 `DocumentSearchService` 一個類別 ——
`IEmbeddingClient` 與 `document_chunks` 都不用動。會先看到的症狀是查詢延遲從
毫秒變成百毫秒級，而不是架構需要重寫。

同理也沒有引入 SIMD 套件（`System.Numerics.Tensors`）。cosine 是十行迴圈，
而那十行的邊界行為（零向量回 0 不回 `NaN`、維度不一致擲例外）我要自己定義並測試。

### Q：怎麼防止 AI 引用一份不存在的 SOP？

這是「不能編造數字」在檢索上的等價問題，答案也是同一個形狀：**把判斷挪到後端**。

1. `source_name` 與 `chunk_index` 由後端從資料表給，system prompt 明文禁止改寫或推測。
   測試斷言回傳的每一組座標都真的存在於 `document_chunks`。
2. 低於相似度門檻的片段根本不會進入工具的回傳值，LLM 看不到就不可能「參考一下」。
   反向驗證：拿掉門檻過濾，5 條測試會紅。
3. `chunks` 為空時 prompt 要求明確說「文件中查不到」，禁止改用模型自己的知識回答 ——
   那會讓使用者以為那是公司文件的規定。
4. `similarity` 不得當成百分比轉述。0.48 不是「48% 相關」。

誠實的邊界：這些保證的是「引用座標不會被捏造」與「低分內容不會外流」。
**「找得準不準」沒有自動化測試涵蓋**，而這個缺口真的咬了我一次 —— 見下一題。

### Q：這個模組最值得講的一次實測是什麼？

第一版的預設 embedding 模型是 `nomic-embed-text`，255 個測試全綠。
裝上真的 Ollama 跑第一輪就發現它在這個語料上不能用：

| 模型 | 7 題相關查詢 top-1 命中 | 相關查詢最高分 | 無關查詢最高分 | 有可用門檻嗎 |
|---|---|---|---|---|
| `nomic-embed-text` | 1/7 | 0.648–0.759 | **0.594、0.622** | **沒有**，區間重疊 |
| `bge-m3` | 4/7（top-3 含正確來源 7/7） | 0.579–0.757 | **0.401、0.456** | 有，0.5 在中間 |

無關查詢是「今天天氣如何？」和「請幫我寫一段 Python 程式」。在 `nomic-embed-text` 下
它們的分數**比真正相關的查詢還高** —— 意思是不存在任何門檻值能把兩者分開，
**防幻覺的第一道防線是假的**。原因是語料與查詢都是中文，那是英文為主的模型；
補上它要求的 `search_document:` / `search_query:` 前綴也沒有改善（仍是 1/7）。

**真正的教訓是「測試驗的是機制，不是效果」**：用假 embedding 的單元測試能驗
「門檻有沒有被套用」（拔掉會紅 5 條），驗不了「門檻值有沒有意義」。
這類缺口只有真的跑一次才會現形 —— 而在跑之前，它被我寫在「已知限制」裡，
當成一條可以接受的限制。

**後來把這個教訓變成了測試**：`RetrievalQualityTests` 接真實 Ollama 跑，
核心斷言是「與語料無關的問題必須回空結果」——
門檻值有沒有意義，等價於無關的問題會不會被擋下來。
把模型換回 `nomic-embed-text` 實測紅 5 條，所以它不是一組裝飾用的測試。
CI 上會真的跑它（獨立的 `retrieval-quality` job，裝 Ollama 並 pull 模型），
並用 `RAG_REQUIRE_OLLAMA=1` 讓它不准 skip —— 少了那個開關，環境沒裝起來時
整組會變成 skip 而 job 照樣綠，那是一條假防線。
誠實的邊界：評測集是 30 組手寫標註，出題的人和寫語料的是同一個。
它有 MRR（0.92）與 Recall@3（1.00）當迴歸基準，比原本的 8 組有解析度得多，
但擋不住「題目本身就出得不夠刁鑽」這件事。

### Q：為什麼 `search_documents` 不像其他工具一樣轉呼叫 Application Service？

因為語意檢索是基礎設施能力（embedding 的 HTTP 呼叫與向量運算），不是領域使用案例。
要讓它經過 Application，就得在那裡定義一個帶相似度分數的回傳型別 ——
Application 因此知道了「有相似度這回事」，而下一步就會有人把門檻判斷搬上去，
那會讓防幻覺的判準離開可測試的位置。

代價是十一個工具裡有一個長得不一樣，這是個需要解釋的結構而不是一眼看懂的結構。
取捨寫在 [rag-module-plan-v1](rag-module-plan-v1.md) 決策 D5，架構測試
（`Domain與Application都不得相依於Rag子系統`）把它釘住。

### Q：要換成 OpenAI 需要改什麼？

只改 `AnthropicLlmClient` 一個類別。`ILlmClient` 與 `Llm*` 模型是供應商中性的，
tool-use 迴圈、`ToolCatalog`、`ToolDispatcher` 都不認識 Anthropic。

規劃文件原本寫這個類別要「純 HTTP 呼叫」，我改用官方 SDK ——
供應商隔離的目的由介面達成，跟底下用不用 SDK 無關，手刻 HTTP 去組
`tool_use`/`tool_result` 的 union 結構只是額外放棄型別安全。

### Q：為什麼用 SQLite 而不是 SQL Server？

作品集的價值在架構與 AI 模組，而「對方能在自己機器上一行指令跑起來」比技術棧書面一致更有說服力。
換 provider 的成本大約一次工作階段，隨時可以做。

**這裡我犯過一個錯，值得講。** 我原本在文件和註解裡寫「SQLite 把 decimal 存成 TEXT，
資料庫層無法正確比較或排序」，還打算為此加一條約束測試。實測後發現是錯的 ——
EF Core 的 SQLite provider 會註冊 `ef_compare()`、`ef_sum()` 與 `EF_DECIMAL` collation：

```sql
WHERE ef_compare("i"."OnHandQty", '50.0') > 0
ORDER BY "i"."OnHandQty" COLLATE EF_DECIMAL
```

如果沒實測就照著寫下去，會做出一條保護不存在問題的測試。現在改成
`SqliteDecimalBehaviourTests` 把真實行為釘住。真正的限制是這些函式只存在於
EF Core 開的連線，原生 SQL 查同一個檔案會退回字典序。

### Q：你的測試怎麼證明它們真的有效？

**把防線拔掉，看測試會不會紅。** 這個專案每一條關鍵防線都做過反向驗證：

| 拔掉什麼 | 結果 |
|---|---|
| 可用庫存改回帳上庫存 | 紅 1 條 |
| 多階 BOM 的累乘 | 紅 4 條 |
| MRP 的逾期工單納入 | 紅 2 條 |
| 採購單查詢的批次化（改回 N+1） | 紅 1 條（查詢次數 +6 vs +2） |
| 多個 tool_result 拆成多則訊息 | 紅 1 條 |
| 未預期例外的總括攔截 | 紅 4 條 |
| 錯誤的 `error_code` 欄位 | 紅 13 條 |
| 稽核 log | 紅 5 條 |
| 灌種子資料的併發容忍 | 紅 2 條（3 次執行都紅，錯誤訊息與原始 flaky 一致） |
| RAG 的相似度門檻過濾 | 紅 5 條（含「低於門檻的片段文字完全不會出現在回傳內容裡」） |
| cosine 除以模長那一步 | 紅 10 條（含「等比例放大的向量相似度仍是 1」） |
| `EmbeddingUnavailableException` 的攔截 | 紅 4 條（降級成 `INTERNAL_ERROR`，證明新錯誤碼真的有作用） |
| 在 Application 加一個向量套件並用 `typeof` 引用 | 紅 1 條（預防性清單那條組件參考檢查） |
| 把 embedding 模型換回 `nomic-embed-text`（接真實 Ollama） | 紅 5 條（3 條「無關查詢必須回空結果」+ 分離度 + 1 條相關查詢）|
| 風險工單的查詢起點改回本週一 | 紅 1 條（上週就逾期的工單看不到了） |
| MRP 排除「已全數發料工單」那個判斷 | 紅 3 條（含兩個種子情境測試） |
| 時間分期第一桶不回溯逾期需求 | 紅 1 條 |
| 時間分期的水位改成不累進 | 紅 4 條 |
| 不把對話歷史組進下一次請求 | 紅 4 條 |
| 對話記憶的輪數截斷 | 紅 1 條 |
| 對話記憶改註冊成 scoped | 紅 2 條（每個請求拿到空的 store，功能安靜失效） |
| 相似度門檻從 0.5 調到 0.7 | 紅 17 條（MRR 掉到 0.357、Recall@3 掉到 0.381） |
| 角色檢查只留工具清單過濾、拿掉執行層那道 | 紅 2 條 |
| 對話記憶的鍵不分角色 | 紅 1 條（換個角色就讀得到別人的歷史） |
| 採購建議 repository 加上 `AsNoTracking` | 紅 2 條（核准「成功」但什麼都沒寫進去） |
| ONNX 特徵向量的前兩欄對調 | 紅 1 條（**只有**黃金樣本那條抓得到） |
| 機率取第 0 欄（不延遲）而不是第 1 欄 | 紅 2 條 |
| 在 Application 參考 ONNX 套件 | 紅 1 條（架構測試的組件參考檢查） |
| 測試工廠改回 `AddInMemoryCollection` 設連線字串 | 紅 1 條（**而且只有驗「DbContext 實際連到哪」的那條會紅**） |

沒有紅的測試等於沒有測試。**這個習慣抓到過一次我自己的錯誤**：
架構測試第一次反向驗證是綠的，我一度以為測試無效，深挖後發現是實驗寫錯了
（`nameof` 不產生型別參考），改用 `typeof` 就紅了。

### Q：開發過程中最有價值的 bug 是哪個？

**一個 flaky 測試，症狀是「每個 build 組態的第一次執行才失敗」。**
錯誤是 `UNIQUE constraint failed: bom_lines...`。重跑五次都通過，很容易就當成環境雜訊放過。

根因是灌種子資料不具備併發安全：`WebApplicationFactory` 會建立 host 不只一次，
冷啟動時兩次 seeding 真正重疊，雙方都通過了「是否已有資料」的檢查；
熱身之後第一次太快完成，第二次就只看到資料而跳過 —— 這就是為什麼只有第一次會炸。

**調查過程中我犯過一個錯**：第一次寫的併發測試顯示「併發 seeding 成功」，
差點就據此排除這個方向。但那個測試用 in-memory SQLite —— 共用單一連線，
寫入天然被序列化，根本測不出併發。改用檔案 SQLite 才重現得出來。
這件事的教訓是：**測試環境與真實環境的差異本身就會製造偽陰性**。

這也不只是測試問題 —— 多個 API 實例同時啟動時，生產環境是一模一樣的競態。

**MRP 漏算逾期工單。** 加端到端測試時，用種子資料算出來的毛需求跟我預期的數字對不上。
第一反應是測試寫錯，但追下去發現是實作的問題：查詢起點用「今天」，
把交期已過但還沒做完的工單整批擋掉了 —— 那些工單仍然要料，而且是最急的需求。

這個 bug 用假的 Repository 測不出來，因為 fake 跟真實 Repository 有同樣的過濾邏輯；
是「用真資料庫 + 真種子資料跑端到端」才浮出來的。

另一個是 **SDK 把中文逃逸成 `\uXXXX`**，實測讓請求體積變成 2.28 倍，
而工具說明每一輪都重送 —— 這是每次呼叫都在付的 token 成本。
SDK 沒有序列化設定點，用 `DelegatingHandler` 在送出前重新序列化解決。

---

## 5. 已知限制（會被問到，先準備好）

- **`AnthropicLlmClient` 尚未對真實 API 驗證過。** 送出的 HTTP 請求內容用本機假伺服器
  逐欄檢查過，但沒有金鑰就無法確認 Anthropic 會接受它。
  這是目前唯一「編譯過、測試過、沒真的跑過」的環節。
  （RAG 那一側原本也在這份清單上，裝了 Ollama 實跑後關閉了 —— 而那一輪實測換掉了
  預設的 embedding 模型，見第 4 節最後一題。）
- **RAG 的相似度門檻擋不住「主題沾得上邊、但語料沒寫」的問題**（售價、付款條件、
  賠償金額實測 0.55–0.63，與真正相關題目的區間重疊）。防線在 system prompt 而不是門檻。
- **RAG 檢索品質的評測集是 30 組手寫標註**，不是業界標準評測集。
  `RetrievalQualityTests`（9 個）在 CI 上會真的跑（獨立 job），本機沒有 Ollama 時 skip。
- **RAG 索引不會自動更新**：語料是編譯進程式的常數，改了要刪掉資料庫重建。
  沒有文件上傳端點 —— 那會帶出權限、病毒掃描、檔案儲存一整串與本模組無關的問題。
- **相似度門檻 0.5 是對「`bge-m3` + 這個語料」量出來的值，不是通用常數。**
  換 embedding 模型後必須重新量。
- **角色隔離只到工具層級**：允許的工具仍然查得到全庫資料，也沒有身分驗證 ——
  `role` 是呼叫端自己填的。它擋得住「工具清單一視同仁地攤開給每個人」，
  擋不住刻意越權的人。
- **`/api/mrp/shortages` 把需求日收斂到最早的那張工單**，建議採購會偏保守。
  刻意保留 —— 「什麼時候開始缺」由 `/api/mrp/time-phased` 回答，兩個端點各司其職。
- **ML 延遲風險模型用的是模擬資料**，展示資料的特徵還落在訓練分布之外。
  沒有分布檢查、沒有機率校準、沒有監控與重訓。
- **BOM 展開是逐階查詢**，深層 BOM 會放大成本。正解是遞迴 CTE，目前資料量下不構成問題。

---

## 6. 授權

[MIT](../LICENSE)。著作權人是 repo 擁有者，程式碼可自由使用、修改與再散布，
但不附任何擔保。原始碼在 https://github.com/thothawei/manufacturing-erp
