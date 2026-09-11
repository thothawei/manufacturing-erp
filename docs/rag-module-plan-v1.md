# 製造業 ERP 系統 — 非結構化文件語意檢索（RAG）模組規劃（v1）

> 這是動工前的規劃，性質與 [`ai-assistant-module-plan-v1.md`](ai-assistant-module-plan-v1.md) 相同：
> 列出範疇、資料流與決策點，**不包含任何已完成的實作**。
> 第 8 節的七個決策點尚未拍板，確認後才開始寫程式。

目標：為現有的 AI 助理加上第 9 個唯讀工具 `search_documents`，
讓它除了查結構化的 ERP 資料，也能從非結構化文件（SOP、維修手冊、客訴紀錄）裡找出相關段落。

技術棧已定，本文件不重新評估：

| 項目 | 選擇 | 不選的理由 |
|---|---|---|
| Embedding | 本機 Ollama HTTP（`/api/embeddings`） | 雲端 embedding API 需要金鑰與網路，違反「clone 下來就能跑」 |
| 向量儲存 | 既有 SQLite 新增一張表 | Qdrant／pgvector 要多跑一個服務，語料只有數十個片段時沒有收益 |
| 相似度 | C# 端 brute-force cosine | 同上；ANN 索引在這個量級下是純粹的複雜度 |

---

## 1. 範疇

### 做什麼

| 項目 | 內容 |
|---|---|
| 新工具 | `search_documents(query, top_k?)`，唯讀，回傳片段文字＋來源＋相似度分數 |
| 新子系統 | `Infrastructure/Rag`，與 `Infrastructure/AI`、`Infrastructure/Persistence` 平行 |
| 新資料表 | 一張 `document_chunks`（片段文字、來源、向量） |
| 語料 | 隨種子資料一起產生的展示文件（決策點 D1） |
| 測試 | 比照既有四層，外加相似度邊界、防幻覺門檻、服務不可用三組（第 7 節） |

### 明確不做

- **不做 reranking、不做 query rewrite、不做 HyDE**。這些都會讓「答案從哪來」變得難追，
  與既有的防幻覺原則（所有數字由後端算好、LLM 不做二次判斷）方向相反。
- **不做跨請求對話記憶**。既有限制照舊，RAG 不改變它。
- **不做文件上傳端點**。語料是展示資料的一部分，由 seeder 產生；上傳會帶出權限、
  病毒掃描、檔案儲存一整串與本模組無關的問題。
- **不讓 LLM 判斷相關性**。相似度由後端算，門檻由後端判，低於門檻就回空結果（決策點 D4）。
- **不動既有 8 個工具**。`ToolCatalog` 與 `ToolDispatcher` 只是各加一個成員。

---

## 2. 資料流

### 2.1 建索引（寫入，一次性）

```
ErpDbSeeder（或獨立的 RagIndexSeeder）
   → 讀取內嵌的展示文件（純文字常數或 embedded resource）
   → Chunker 切段（決策點 D2）
   → IEmbeddingClient.EmbedAsync(每段文字)  ──HTTP──▶  Ollama /api/embeddings
   → document_chunks 寫入（source_name、chunk_index、text、embedding、model、dimension）
```

Ollama 不可用時**不能讓 seeding 失敗**——核心 ERP 與既有 8 個工具都不依賴它（決策點 D3）。

### 2.2 查詢（讀取，每次工具呼叫）

```
LLM 要求 search_documents(query="面板色偏怎麼處理", top_k=3)
   → ToolDispatcher（既有，加一個 case）
   → DocumentSearchService（Infrastructure/Rag）
        ① IEmbeddingClient.EmbedAsync(query)  ──HTTP──▶ Ollama
        ② 從 document_chunks 載入全部向量
        ③ brute-force cosine similarity，排序取 top_k
        ④ 過濾低於門檻的片段
   → 回傳 { chunks: [{ text, source_name, chunk_index, similarity }], ... }
   → 稽核 log：工具名稱、成功與否、耗時、參數、命中幾段、最高分
   → LLM 只負責把片段組成中文回答並附上來源
```

**第 ④ 步是防幻覺的關鍵位置**：門檻判斷留在後端，LLM 拿到的要嘛是確實相關的片段，
要嘛是空結果。讓 LLM 自己看分數決定「這段算不算相關」，等於把判準交給無法測試的一方。

### 2.3 成本估算（語料 8 份文件、約 7000 字、約 30 個片段）

| 項目 | 估算 | 依據 |
|---|---|---|
| 向量儲存空間 | 1024 維 × 4 bytes × 33 ≈ 132 KB | 實測（`bge-m3` 是 1024 維；規劃時假設的 `nomic-embed-text` 是 768 維，但實測後換掉了，見 11.2） |
| 單次查詢的乘加次數 | 1024 × 33 ≈ 34,000 | brute-force，微秒級，不需要索引 |
| 單次查詢的實際瓶頸 | Ollama embed 一次 query 的往返 | **已實測**：單次查詢 19–80 ms，絕大部分是那一次往返 |
| 建索引耗時 | 33 次 embed 往返 | **已實測**：1969 ms |

語料規模刻意壓在這個量級：brute-force 的效能疑慮在數十個片段時不存在，
而被問到「幾萬份文件怎麼辦」時，答案是換 ANN 索引而不是改架構——這點寫進 README 的已知限制。

---

## 3. 架構邊界

### 3.1 分層

```
src/Erp.Infrastructure/
  AI/            既有：ILlmClient、ToolCatalog、ToolDispatcher、tool-use 迴圈
  Persistence/   既有：EF Core
  Rag/           新增：IEmbeddingClient、OllamaEmbeddingClient、Chunker、
                 VectorMath、DocumentSearchService、RagOptions、
                 EmbeddingUnavailableException
```

三個子系統平行，**Rag 不得相依於 AI**（反之可以：`ToolDispatcher` 會呼叫 `DocumentSearchService`）。
這與既有的「Persistence 不得相依於 AI」同一個形狀，架構測試照抄即可。

`document_chunks` 的 EF 設定放 `Persistence/Configurations`（資料表設定本來就屬於那裡），
但 **Domain 不會有 `DocumentChunk` 實體**——它不是領域概念，是檢索索引。
放 Domain 會讓 Domain 間接背上「向量是什麼」這個知識。

### 3.2 Domain／Application 的隔離，以及一個必須承認的落差

使用者要求「比照 ArchitectureTests 對 LLM SDK 的隔離方式，用組件參考檢查而不是命名空間規則」。
這裡有個誠實的問題：**如果 Rag 只用 `HttpClient` 與手寫 cosine，就沒有任何外部組件可以檢查**。
LLM SDK 那條測得到，是因為 `Anthropic` 是一個具名組件；純 HTTP 呼叫在 IL 裡留不下「這是 embedding」的痕跡。

能做到的三條（前兩條是真防線，第三條是補位）：

| 檢查 | 形式 | 擋得住什麼 |
|---|---|---|
| Domain／Application 不得相依 `Erp.Infrastructure.Rag` | NetArchTest 命名空間 | 直接 using 本模組的型別 |
| Rag 不得相依 `Erp.Infrastructure.AI`，AI 可相依 Rag | NetArchTest 命名空間 | 子系統方向反轉 |
| Domain／Application 不得參考向量運算套件 | 組件參考檢查 | **只在決策點 D7 選了 `System.Numerics.Tensors` 時才有標的** |

若 D7 選手寫 cosine，第三條就是一條永遠會綠的空測試——那會重演 v3 文件裡那個
「差點做出一條保護不存在問題的測試」的錯誤，寧可不寫，並在文件裡寫明為什麼不寫。

### 3.3 `search_documents` 要不要經過 Application？

既有 8 個工具都是「薄薄一層轉呼叫 Application Service」。第 9 個若直接呼叫
`Infrastructure/Rag` 的服務，會打破這個對稱性；但若經過 Application，Application 就得定義
一個 port，而那個 port 的回傳型別必須完全不含向量（否則違反隔離要求）。
這是**決策點 D5**，不在這裡拍板。

---

## 4. 工具契約

```
search_documents(query: string, top_k?: integer)
```

| 參數 | 必填 | 說明 |
|---|---|---|
| `query` | 是 | 要查的問題或關鍵描述，自然語言 |
| `top_k` | 否 | 最多回傳幾個片段，預設 3，上限 10（超過上限回 `INVALID_ARGUMENT`） |

成功回傳（形狀待 D4 拍板）：

```json
{
  "chunks": [
    {
      "text": "面板色偏判定以標準光源箱比對色票…",
      "source_name": "品管異常處理 SOP",
      "chunk_index": 4,
      "similarity": 0.82
    }
  ],
  "similarity_threshold": 0.5,
  "matched_count": 1
}
```

查不到（全部低於門檻）時回**成功但空清單**，不是錯誤——查無資料是正常結果，
這與既有 `search_items` 的處理一致（`ToolCatalog` 裡明寫「查無資料時回傳空清單」）。

失敗時沿用既有契約 `{ "error_code": ..., "message": ... }`：

| error_code | 情境 |
|---|---|
| `INVALID_ARGUMENT` | `query` 缺漏或為空、`top_k` 不是整數或超出範圍 |
| 服務不可用（碼待定） | Ollama 沒裝、沒啟動、回非 2xx、逾時 → **決策點 D6** |
| `INTERNAL_ERROR` | 其餘未預期例外，由既有的總括攔截處理 |

### 4.1 三個地方要一起改（README 已寫明的規則）

| 位置 | 改什麼 |
|---|---|
| `ToolCatalog.All` | 新增 `SearchDocuments` 常數與 `ToolDefinition` |
| `ToolDispatcher` 的 switch | 新增一個 case |
| `ToolCatalogConsistencyTests.SampleValue` | 補 `query` 與 `top_k` 的範例值 |

**這裡有一個既有測試會被卡住的真問題**：一致性測試會斷言每個工具「帶必填參數」與
「帶全部參數」都不回錯誤。`search_documents` 在 CI 上沒有 Ollama，必然回錯誤 →
這兩條 Theory 會紅。解法是 `TestServices.CreateDispatcher` 注入一個固定向量的
假 `IEmbeddingClient`，讓一致性測試驗「接得上」而不是驗「Ollama 活著」。
這也是 `IEmbeddingClient` 必須存在的真正理由——不是為了將來換供應商，是為了測試能離線跑。

### 4.2 稽核 log

既有 `ToolDispatcher.ExecuteAsync` 的那一行已經記了工具名稱、成功與否、耗時、參數。
RAG 要多記「命中幾個片段」與「最高相似度」——這兩個數字是事後判斷
「它說查不到，是真的沒有，還是門檻調太高」的唯一依據。

做法上不該把 RAG 專屬欄位塞進通用的那行 log（那會讓 8 個工具都多兩個永遠是空的欄位），
而是由 `DocumentSearchService` 自己記一筆檢索層的 log，兩筆透過工具名稱對得起來。

---

## 5. 資料表

```
document_chunks
  id              INTEGER PK
  source_name     TEXT     來源文件名稱（給 LLM 引用用）
  chunk_index     INTEGER  在該文件中的段落序號，從 0 起
  text            TEXT     片段原文
  embedding       BLOB     向量（格式見 D7）
  embedding_model TEXT     產生這個向量的模型名稱
  dimension       INTEGER  維度
  UNIQUE(source_name, chunk_index)
```

`embedding_model` 與 `dimension` 不是裝飾：換模型後維度不同，舊向量與新 query 向量
算 cosine 會得到一個「有數字但沒有意義」的結果。查詢時必須比對模型名稱，
不一致就當成索引失效（回服務不可用／需重建），而不是硬算。

---

## 6. Ollama 不可用的行為

| 情境 | 行為 |
|---|---|
| 啟動時 Ollama 沒跑 | **照常啟動**，核心 ERP 與既有 8 個工具完全不受影響 |
| 建索引時 Ollama 沒跑 | seeding 記一條 Warning 後跳過 RAG 索引，不拋例外 |
| 查詢時 Ollama 沒跑 | `search_documents` 回明確 error_code，不是未預期例外（D6） |
| 查詢時索引是空的 | 回明確 error_code（「索引尚未建立」與「沒查到」是不同的事，不能混為空結果） |
| 模型沒 pull | Ollama 會回 404／錯誤 JSON，轉成同一個 error_code，message 寫明要 `ollama pull` |

連線逾時要設短（建議 10 秒，可設定）。比照 `AiAssistantOptions.TimeoutSeconds` 的理由：
`HttpClient` 預設 100 秒，對一個互動式查詢來說使用者早就放棄了。

「沒裝 Ollama」這條路徑在這台開發機上**可以真的驗證**（實測：`ollama` 不在 PATH，
`localhost:11434` 無回應），這與 Anthropic 金鑰那個缺口相反。
但反過來說，**embedding 的正常路徑會成為第二個「未對真實服務驗證」的缺口**，
除非本機裝上 Ollama 跑一次。處理方式比照 `AnthropicWireFormatTests`：
用本機假 HTTP 伺服器接住真正送出的請求逐欄檢查（`model`、`prompt` 欄位名、中文不被逃逸），
並在 README 誠實標明「`OllamaEmbeddingClient` 尚未對真實 Ollama 驗證過」直到實際跑過一次。

---

## 7. 測試策略

比照既有四層，估計新增 30–40 個測試。

| 專案 | 新增內容 |
|---|---|
| `Erp.Application.Tests` | 若 D5 選「經過 Application」才有新增；否則無 |
| `Erp.Infrastructure.Tests` | 主戰場：相似度數學、chunking、檢索排序、門檻、錯誤契約、假 Ollama wire format、稽核 log |
| `Erp.Api.Tests` | 若 D6 新增錯誤碼且需對映 HTTP 狀態碼才有新增 |
| `Erp.ArchitectureTests` | 3.2 那張表的前兩條（第三條視 D7） |

### 7.1 相似度計算正確性（邊界案例）

| 案例 | 期望 |
|---|---|
| 兩個相同向量 | 1.0（允許浮點誤差） |
| 正交向量 | 0.0 |
| 反向向量 | −1.0 |
| 等比例放大的向量 | 與原向量相似度 1.0（cosine 與長度無關，這條證明沒漏除以模長） |
| 零向量 | 不得回 `NaN`，也不得除以零 → 明確定義回 0 或視為無效 |
| 維度不一致 | 擲明確例外，不得靜默比較前 N 維 |
| 單維向量、極小值（1e-30） | 不溢位、不回 `NaN` |
| 已知向量組的排序 | 手算出答案，斷言排序結果，**而不是只斷言「有回東西」** |

反向驗證：把除以模長那一步拿掉，第 4 條必須紅。

### 7.2 防幻覺

| 測試 | 驗什麼 |
|---|---|
| 全部片段低於門檻 | 回成功＋空 `chunks`，**不回任何片段文字** |
| 索引為空 | 回錯誤碼而非空結果（兩者語意不同） |
| 片段的 `source_name` 與 `chunk_index` 必須真的對得上資料表 | 引用不能是捏造的 |
| `top_k` 大於命中數 | 回實際命中數，不補湊 |
| system prompt 覆蓋測試 | 新增的那條規則（引用必須來自工具回傳的 `source_name`／查不到要老實說）存在。**不驗 LLM 是否遵守**——那需要真實 API 與行為評測，比照既有 `SystemPromptTests` 的立場 |
| tool-use 迴圈測試 | 用 `FakeLlmClient` 腳本化「LLM 呼叫 search_documents 得到空結果」，確認迴圈不崩、把空結果如實送回 |

反向驗證：把門檻過濾拿掉，第 1 條必須紅。

### 7.3 Ollama 服務不可用

| 測試 | 做法 |
|---|---|
| 連線被拒 | 指向一個沒人在聽的 port，斷言回指定 error_code 而非未預期例外 |
| 回 500 | 假伺服器回 500 |
| 回 404（模型沒 pull） | 假伺服器回 404，斷言 message 提到 `ollama pull` |
| 回格式錯誤的 JSON | 假伺服器回 `{}` 或缺 `embedding` 欄位 |
| 逾時 | 假伺服器延遲超過設定值 |
| 錯誤訊息不洩漏內部細節 | 回給 LLM 的 message 不含例外型別、堆疊、URL 以外的內部資訊 |
| 一個工具掛掉不影響同輪其他工具 | 比照既有 G1 的那條測試，這是整個錯誤處理的價值所在 |

反向驗證：移除 `OllamaEmbeddingClient` 的例外轉換，前五條必須紅。

---

## 8. 決策點（已拍板）

前四個是使用者指定的；後三個是寫這份規劃時才浮現、但會影響程式碼形狀的。
**七個都已拍板，結論列在下表，各選項的取捨完整保留在後面各小節** ——
日後要翻案時，翻的是同一份取捨而不是重新調查。

| # | 決策 | 結論 | 翻轉條件 |
|---|---|---|---|
| D1 | 語料範疇 | **B**：6–8 份、約 7000 字、約 30 片段 | 語料撰寫成本過高時退回 A |
| D2 | Chunking | **A**：依段落切、不重疊；門檻寫設定、初值 0.5 | 實測發現答案常被切斷時再加重疊 |
| D3 | Ollama 呈現 | **A**：README 標為可選 + 啟動 log 印索引狀態 | 展示時 LLM 頻繁呼叫註定失敗的工具才考慮 B |
| D4 | 迴圈整合 | **A**：後端過濾，低分片段不回傳 | 使用者抱怨「不知道是沒資料還是門檻太高」時考慮 B |
| D5 | 分層位置 | **A**：完全留在 Infrastructure | 日後出現非 AI 的檢索需求（REST 端點）時再上推 |
| D6 | 服務不可用錯誤碼 | **A**：新增 `SERVICE_UNAVAILABLE` | 無 |
| D7 | 儲存與實作 | **BLOB float32 + 手寫 cosine**，不引入任何套件 | 語料成長到數千片段、brute-force 成為瓶頸時再引入 SIMD |

D7 連帶的一個調整，寫在這裡以免日後誤讀為疏漏：既有「Domain/Application 不得參考 LLM 廠商套件」
那條組件參考檢查，在 RAG 這邊改為**預防性清單**（`System.Numerics.Tensors`、`Microsoft.ML`、
`Ollama*`、`Qdrant*`、`Pinecone*` 等）。不引入套件的選擇讓這條檢查現在沒有任何命中，
它防的是「日後有人在 Application 裝一個向量套件」。反向驗證的方式是臨時在 Application
加一個命中清單的 PackageReference 並實際使用，確認測試會紅後還原 —— 不靠宣稱。

### D1 文件語料範疇

沿用 TV-100 的展示情境（面板可用 80 片、WO-01 延遲 2 天、面板淨缺 130 片），
讓 RAG 的答案能跟結構化查詢串成同一個故事。

| 選項 | 內容 | 取捨 |
|---|---|---|
| **A：3 份文件、約 3000 字、約 12 片段** | 品管異常處理 SOP、設備維修手冊摘要、客訴處理紀錄各一份 | 夠展示、寫起來快；但片段少，top_k=3 幾乎等於回傳四分之一個語料庫，「檢索」的說服力弱 |
| **B：6–8 份文件、約 7000 字、約 30 片段** | 上述三類各 2–3 份（例如 SOP 分「色偏」「異音」「尺寸超差」三件） | 檢索行為看得出區別（問色偏不會回異音那篇）；語料撰寫是本模組最耗時的部分，且每份都要真的講得通，不能是填充文字 |

兩個選項對 brute-force 效能都毫無壓力（第 2.3 節）。差別只在「展示時看起來像不像真的在檢索」。

與既有情境的掛鉤建議（兩個選項都適用）：SOP 提面板色偏的判定與處置、
維修手冊提貼合機的校正與前置期、客訴紀錄提某批號色偏客訴——
這樣「WO-01 為什麼延遲」可以由結構化工具回答，「色偏該怎麼處理」由 RAG 回答，
兩者在同一次對話裡互補而不重疊。

### D2 Chunking 策略

| 選項 | 做法 | 取捨 |
|---|---|---|
| **A：依段落切，不重疊** | 以空行為界切段，過長的段落（>400 字）再按句號切 | 實作最簡單，`chunk_index` 就是段落序號，引用天然精確；跨段落的答案會被切斷（「判定標準」在第 3 段、「處置方式」在第 4 段時，只命中一段會答不完整） |
| **B：依段落切＋固定重疊** | 同上，但每段往前帶上一段的最後 1–2 句（約 50 字） | 跨段落的答案較完整；重疊會讓同一句話出現在兩個片段，top_k=3 可能回三個高度重複的片段，而且 `chunk_index` 與原文段落不再一對一，引用的精確度下降 |

來源保留（兩個選項都採用，這不是取捨）：每個片段都帶 `source_name` ＋ `chunk_index`，
工具回傳時一併給 LLM，system prompt 要求回答必須標出來源。
等價於既有的「所有數字由後端算好」——**引用的來源字串只能來自工具回傳值，不能由 LLM 生成**。
測試上的落地是 7.2 第 3 條：斷言回傳的 `source_name`／`chunk_index` 對得上資料表。

門檻值（與 D2 連動，建議一起拍板）：cosine 相似度門檻初值建議 0.5，寫進設定而不是寫死——
門檻是唯一需要看實際語料微調的參數，寫死的話每次調都要改程式碼。
需要注意 `nomic-embed-text` 的相似度分布偏高（不相關的文字也常在 0.3–0.5），
確切門檻要等語料與模型都定了、實際跑過才知道，規劃階段給的是起點不是結論。

> **實作後補註**：上面這段只對了一半。實測 `nomic-embed-text` 在中文語料下，
> 不相關的文字落在 **0.59–0.62**（不是 0.3–0.5），而且**比相關查詢還高** ——
> 問題不是「門檻難定」而是「沒有任何門檻能分開兩者」。已換成 `bge-m3`，見 11.2 第 0 條。
> 「實際跑過才知道」這句話本身是對的，代價是它要真的被執行。

### D3 Ollama 依賴的呈現方式

| 選項 | 做法 | 取捨 |
|---|---|---|
| **A：README 明標為可選模組** | 在 README 開頭的導覽表與「AI 助理」節之後新增一節「文件語意檢索（可選）」，寫明需要額外裝 Ollama 並 `ollama pull`，且**沒裝時核心 ERP 與既有 8 個工具完全正常**；啟動 log 比照金鑰來源那行，印出「RAG 索引：已建立 N 段／未建立（Ollama 未啟動）」 | 評審 clone 下來不會誤以為專案壞掉；README 再長一節 |
| **B：A 再加上「降級的工具目錄」** | Ollama 不可用時，`ToolCatalog` 不把 `search_documents` 送給 LLM | LLM 不會呼叫一個註定失敗的工具，對話更乾淨；但工具目錄變成動態的，`ToolCatalogConsistencyTests` 的「走訪每個工具」語意跟著變（要走訪哪一份目錄？），而且「沒裝 Ollama 時會回清楚錯誤」這條防線在正常流程中永遠不會被走到——防線還在，但不再是使用者可見的行為 |

選 A 時，啟動時的探測要非阻塞：不該為了印一行 log 讓啟動等一個 10 秒逾時。

### D4 與既有 tool-use 迴圈的整合

共同點（不是取捨）：工具只回片段文字、來源、相似度；LLM 只組織語言，不做二次相關性判斷。

| 選項 | 回傳形狀 | 取捨 |
|---|---|---|
| **A：回片段＋分數，門檻由後端過濾** | 低於門檻的片段根本不出現在回傳值裡；`chunks` 為空時另附一句 `"沒有找到相似度達到門檻的段落"` | 防幻覺最強：LLM 看不到低分片段，不可能「參考一下」；門檻調整純屬後端決定，但 LLM 無從區分「語料裡沒這個主題」與「門檻設太高」 |
| **B：回片段＋分數，另附被過濾掉的數量** | 同上，但多一個 `filtered_out_count` 與 `top_similarity_below_threshold` | LLM 可以說「有相關性較低的段落，但未達引用標準」，使用者體驗較好；多兩個欄位就多兩個 LLM 可能誤用的數字（例如拿 0.48 當成「有 48% 相關」轉述），需要在工具說明裡明確禁止 |

`top_k` 的預設與上限（建議 3／10）兩個選項共用。一致性測試必須真的帶 `top_k` 執行一次
（使用者明確要求，也是既有測試的既定做法）。

### D5 `search_documents` 要不要經過 Application 層？

| 選項 | 做法 | 取捨 |
|---|---|---|
| **A：完全留在 Infrastructure** | `ToolDispatcher` 直接呼叫 `Infrastructure/Rag/DocumentSearchService` | 隔離要求自動滿足，Application 連一行都不用改；打破「9 個工具都轉呼叫 Application Service」的對稱性，被問到「為什麼這個工具不一樣」時要解釋（答案是成立的：檢索是基礎設施能力，不是領域使用案例——但這是個需要講的解釋，不是一眼看懂的結構） |
| **B：Application 開一個不含向量的 port** | Application 定義 `IDocumentSearchPort`，回傳 `record DocumentExcerpt(string Text, string SourceName, int ChunkIndex, double Similarity)`，實作在 Infrastructure/Rag | 與既有 8 個工具結構一致，依賴反轉的說法也一致；`Similarity` 是 cosine 分數——它算不算「向量相關型別」？嚴格說不算（它是一個 double），但 Application 因此知道了「有相似度這回事」，隔離要求變成「字面上過關、精神上鬆動」 |

這是七個決策點裡最該想清楚的一個：它決定了新程式碼長在哪一層，事後搬很貴。

### D6 服務不可用的 error_code

現有五碼（`ENTITY_NOT_FOUND`／`INVALID_ARGUMENT`／`NOT_APPLICABLE`／`UNKNOWN_TOOL`／`INTERNAL_ERROR`）
沒有一個適合「依賴的外部服務沒跑」。

| 選項 | 做法 | 取捨 |
|---|---|---|
| **A：新增 `SERVICE_UNAVAILABLE`** | 加 enum 成員與 wire 字串，system prompt 補一條逐碼規則（「告訴使用者文件檢索功能暫時無法使用，可改用結構化查詢，不要重試」） | LLM 能區分「程式壞了」與「這台機器沒裝 Ollama」，後者可以建議使用者改問別的；錯誤碼是對外契約，加一個就要同步改 system prompt、完整性測試、README 的錯誤碼表三處（既有的完整性測試會在忘記加 wire 字串時直接紅，這點是好事） |
| **B：複用 `INTERNAL_ERROR`** | 不動錯誤碼 | 零擴散，既有測試與文件全不用改；「環境沒裝 Ollama」被歸類成系統錯誤，LLM 只能說「查詢失敗」，而這其實是最常見、最該給出明確指引的情境 |

若選 A，還要決定要不要在 `ErpExceptionHandler` 對映 HTTP 狀態碼——
目前 RAG 只經由工具呼叫進入，沒有獨立的 REST 端點，所以可以不碰；
但若日後加 `GET /api/documents/search`，503 是正確的答案（與 `LlmUnavailableException` 一致）。

### D7 向量儲存格式與相似度實作

| 選項 | 做法 | 取捨 |
|---|---|---|
| **A：BLOB（float32 陣列）＋手寫 cosine** | `MemoryMarshal` 直接把 `byte[]` 當 `float[]` 讀 | 空間小（768 維＝3072 bytes）、讀取零解析成本；用 `sqlite3` CLI 看不到內容（除錯時只能看到一團二進位）；位元組順序是隱含約定，要寫進註解 |
| **B：JSON 文字＋手寫 cosine** | `[0.013,-0.024,…]` | 可讀、可用 CLI 檢查、跨語言相容；空間約 3–4 倍（768 維約 10–12 KB／段），每次查詢都要解析 30 段 JSON——在這個量級仍是毫秒級，但它是「為了除錯方便而付的常態成本」 |

相似度實作另有一個獨立的選擇：手寫迴圈，或引入 `System.Numerics.Tensors`
（`TensorPrimitives.CosineSimilarity`，SIMD 加速）。

- 手寫：零新依賴，邊界行為（零向量、維度不一致）由自己定義，測起來最直接。
- `System.Numerics.Tensors`：效能更好（這個量級下無感），**但它是 3.2 節第三條架構測試唯一可能的標的**——
  有一個具名組件，「Domain／Application 不得參考向量運算套件」才測得出東西。
  代價是多一個 PackageReference，而且邊界行為由套件定義（零向量回什麼要先實測，不能照常識假設）。

決策時請一起回答兩個問題：儲存格式（A／B），以及相似度實作（手寫／套件）。

---

## 9. 工作順序（決策拍板後）

```
R1  資料表 + migration + EF 設定 + Domain 不碰向量的架構測試
      ↓
R2  IEmbeddingClient + OllamaEmbeddingClient + 錯誤轉換 + 假伺服器 wire format 測試
      ↓
R3  Chunker + VectorMath（先寫相似度邊界測試，再寫實作）
      ↓
R4  語料撰寫 + 索引建立（seeder 整合，Ollama 不可用時跳過）
      ↓
R5  DocumentSearchService + 門檻 + 檢索層稽核 log
      ↓
R6  ToolCatalog / ToolDispatcher / SampleValue 三處 + 假 IEmbeddingClient 讓一致性測試離線可跑
      ↓
R7  system prompt 新規則 + 覆蓋測試 + tool-use 迴圈整合測試
      ↓
R8  README 可選模組說明、已知限制、展示腳本；實際裝 Ollama 跑一次關閉驗證缺口
```

R3 刻意排在 R4 之前：相似度的邊界行為不需要語料就能測，先把數學釘死，
之後檢索結果不對時才能排除「是不是 cosine 算錯」這個可能。

---

## 10. 預期會寫進 README 的已知限制

這一節現在就列出來，因為它們是選擇這個技術棧的**必然結果**，不是實作疏漏：

- **brute-force 全表掃描**：每次查詢載入全部向量。數十個片段無感，數萬份文件要換 ANN 索引。
- **沒有 ANN、沒有 reranking、沒有 query rewrite**：刻意不做，理由見第 1 節。
- **索引不會自動更新**：語料是編譯進程式的常數，改了要重建資料庫（與既有種子資料同一個模式）。
- **換 embedding 模型要重建索引**：維度與向量空間都變了，由 `embedding_model` 欄位把關。
- **RAG 功能依賴本機 Ollama**：沒裝時核心 ERP 與既有 8 個工具完全正常，只有第 9 個工具回明確錯誤。
- **`OllamaEmbeddingClient` 尚未對真實 Ollama 驗證過**（直到實際跑過一次為止）——
  與 `AnthropicLlmClient` 同一類缺口，但這個缺口只需要裝一個本機服務就能關閉，不需要儲值。

---

## 11. 實作對帳（完成後補寫）

七個決策全部照拍板執行，沒有中途改道。以下是與規劃不同、或規劃沒預見的地方。

### 11.1 三處與規劃不同

| # | 規劃寫的 | 實際 | 說明 |
|---|---|---|---|
| 1 | 語料約 7000 字 | **3944 字**、7 份、33 片段 | 字數估高了約一倍，但**片段數正中目標**（規劃寫約 30）。片段數才是 brute-force 成本與「檢索看得出區別」的依據，所以沒有為了湊字數去加長語料 —— 那會是為了對齊一個沒有意義的數字而製造工作 |
| 2 | 索引建立放 seeder | 獨立的 `RagIndexBuilder`，由 Api 層在 seeding 之後呼叫 | 放進 `ErpDbSeeder` 會讓 Persistence 相依於 Rag，把兩個平行子系統綁成上下關係。現在方向是 `AI → Rag → Persistence`，並由 `Persistence不得相依於Rag子系統` 這條架構測試釘住 |
| 3 | `DocumentChunk` 的 EF 設定放 `Persistence/Configurations` | 放在 `Rag/DocumentChunk.cs`，`ErpDbContext` 不宣告 `DbSet` | 同一個理由。`ApplyConfigurationsFromAssembly` 會自動掃到，Rag 側用 `db.Set<DocumentChunk>()` 存取，Persistence 對 Rag 的編譯期依賴是零 |

### 11.2 規劃沒預見的四件事

四件都是實作、測試或實跑時撞到的，不是事後回想（詳細敘述在 README 的「實作時踩到的坑」）。
**第一件是其中最重要的，而且它推翻了規劃階段的一個技術選擇**：

0. **`nomic-embed-text` 在中文語料上不可用。** 規劃的技術棧寫「nomic-embed-text 或
   mxbai-embed-large」，實跑後換成 `bge-m3`。關鍵不是 top-1 命中率從 1/7 變 4/7，
   而是**無關查詢（「今天天氣如何」）在 nomic 下拿到 0.62，比相關查詢還高** ——
   不存在任何門檻值能分開兩者，決策 D4-A 那道「後端門檻過濾」的防線形同不存在。
   單元測試全綠（它們用假 embedding，驗的是「門檻有沒有被套用」而不是「門檻值有沒有意義」）。
   補上 nomic 要求的任務前綴也沒有改善。門檻 0.5 因此是對「這個模型 + 這個語料」
   量出來的值，不是通用常數。

1. **一致性測試會被新工具反咬** —— `IEmbeddingClient` 因此從裝飾性抽象變成必需品。
   規劃第 4.1 節已經預見這條，實作時確認了它的嚴重性：沒有假實作，CI 必紅。
2. **完全相同的向量，cosine 不是精確 1.0**（`0.9999999999…`）。
   測試把門檻設在 `1.0` 表達「只有完全相同才過關」時，連它自己都被 `>=` 濾掉。
3. **假 HTTP 伺服器在用戶端逾時後，連設定 `ContentLength64` 都會擲 `ObjectDisposedException`**，
   而那行在 `try` 外面 —— 斷言其實通過了，測試卻在 `Dispose` 時失敗。

### 11.3 四條防線的反向驗證結果

把防線拔掉、確認測試會紅，再還原。沒有紅過的測試等於沒有測試。

| 拔掉什麼 | 結果 |
|---|---|
| `DocumentSearchService` 的門檻過濾 | 紅 5 條（含「低於門檻的片段文字完全不會出現在回傳內容裡」） |
| `VectorMath` 除以模長那一步 | 紅 10 條（含「等比例放大的向量相似度仍是 1」） |
| `ToolDispatcher` 的 `EmbeddingUnavailableException` 攔截 | 紅 4 條（降級成 `INTERNAL_ERROR`，證明新錯誤碼真的有作用） |
| 在 Application 加一個向量套件並用 `typeof` 引用 | 紅 1 條（預防性清單那條組件參考檢查） |
| 把模型換回 `nomic-embed-text`（接真實 Ollama 跑 `RetrievalQualityTests`） | 紅 5 條 —— 這條證明了檢索品質測試不是裝飾 |

**一條無法反向驗證的**：`VectorBlob` 的小端序。在小端序機器上，換成平台預設的
`BitConverter` 行為完全相同，測試不會紅。它防的是資料庫檔案被搬到大端序機器 ——
這條約束靠的是顯式的 `BinaryPrimitives.Write/ReadSingleLittleEndian` 與註解，
測試只能證明「現在是小端序」而不能證明「不依賴平台」。如實記錄，不假裝它被保護了。

### 11.4 實跑驗證過什麼、沒驗過什麼

**驗過**（實際執行、看到輸出）：
- **沒裝** Ollama 時服務照常啟動：`/health` 回 200，啟動 log 印出
  「索引 0 段…（索引未建立：需要本機 Ollama 並執行 ollama pull bge-m3；其他八個工具不受影響）」，
  `RagIndexBuilder` 記一筆 Warning 而不是拋例外。
- **裝了** Ollama 之後（`brew install ollama`、`ollama pull bge-m3`）：
  索引建立完成（7 份文件、33 段、1024 維、1969 ms），啟動 log 印出「索引 33 段，模型 bge-m3」。
- 真實檢索：問「面板色偏的判定標準是什麼」回 SOP 的「適用範圍」與「判定方式」兩段
  （0.7673／0.7645）；問「今天天氣如何」與「請幫我寫一段 Python 程式」**回 0 段** ——
  門檻真的守住了。
- 255 個測試全綠、`dotnet format --verify-no-changes` 通過、Release 建置 0 警告。

**沒驗過**：
- `AnthropicLlmClient` 對真實 Anthropic API 的請求（沒有金鑰）。這是唯一剩下的缺口。
- 檢索品質**已經有自動化測試，但不在 CI 上把關**。`RetrievalQualityTests`（9 個，
  5 相關 + 3 無關 + 1 分離度）接真實 Ollama 跑，本機有就跑、沒有就整組 skip
  —— CI 沒有 Ollama，要它跑得讓 runner 每次下載 1.2 GB 模型。
  標註的問答對只有 8 組，不是正式評測集。

---

## 12. 把事故變成測試（11.2 第 0 條的後續）

選錯模型這件事本身已經修掉了，但「**為什麼單元測試全綠還是放它過關**」這個缺口如果只寫進
文件，下一次換模型時沒有人會記得重量一次。所以補了 `RetrievalQualityTests`。

| 這組測試 | 斷言什麼 | 為什麼這樣設計 |
|---|---|---|
| 相關查詢（5 題） | top-3 要含指定的來源文件 | 用 top-3 而不是 top-1：實際使用是 `top_k=3`，而 top-1 會隨模型小版本漂動，斷言它只會製造脆弱的測試 |
| **無關查詢（3 題）** | **必須回空結果** | **這是會抓到事故的那一條。** 門檻值有沒有意義，等價於「無關的問題會不會被擋下來」 |
| 分離度（1 題） | 相關最低分 − 無關最高分 ≥ 0.1，且門檻落在兩者之間 | 提早警告分離度在縮小 |

**分離度那條不足以抓到原本的事故，這點要講清楚**：nomic 當時是相關 0.648／無關 0.622，
分離度還是正的（0.026），只是小到放不下任何門檻。真正會紅的是「無關查詢回空結果」那組。
把「分離度 ≥ 0.1」這個數字訂在哪裡本身是個判斷，而不是從事故推導出來的。

**為什麼不用 `Assert.Skip`**：xunit 2.9.3 沒有這個 API（寫下去會被解析成
`AsyncEnumerable.Skip` 而編譯失敗）。改用自訂的 `OllamaFactAttribute` /
`OllamaTheoryAttribute`，在屬性建構時同步探測 `/api/tags` 並設定 `Skip`，
結果用 `Lazy` 快取，整個測試回合只打一次。

**副作用**：`Skip` 設在 `TheoryAttribute` 上時 xunit 不會展開資料列，
所以沒有 Ollama 時顯示的是 3 個 skip（2 theory + 1 fact）而不是 9 個。
測試總數因此會隨環境變動 —— 有 Ollama 264、沒有 255 加 3 skip。
文件寫的是 255（CI 的數字），並註明另外 9 個的條件。

**這組測試沒有解決的事**：它在 CI 上是 skip 的，所以這條防線目前靠本機執行。
要變成真正的 CI 把關，得在 workflow 裡裝 Ollama 並 pull 1.2 GB 的模型 ——
那是一個「CI 時間換防線強度」的取捨，還沒做。
