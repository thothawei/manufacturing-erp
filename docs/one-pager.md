# 製造業 ERP + AI 助理 — 一頁式重點

> Clean Architecture 分層的製造業 ERP，加上一個以 tool-use 驅動的 AI 助理：
> 使用者用自然語言提問，AI 只能透過十二個定義好的工具查詢，**所有數字都由後端算好**；
> 唯一會寫入的那個工具寫出來的是「待人工確認的採購建議」，不是採購單。

## 解決什麼問題

現場的人要知道「這批貨到底做不做得出來」，得同時看庫存、BOM、工單、採購四張表，
再自己心算可用庫存與缺料。直接把這些問題丟給 LLM 的做法會編數字 ——
它沒有資料，只能照語感生成一個看起來合理的答案。

這個專案的做法是：**LLM 只負責理解問題與選工具，計算完全不交給它**。
BOM 展開、可製造量、MRP 建議採購量都在 Application 層算完才回給 LLM，
LLM 拿到的是結果，不是原料。

## 技術亮點

1. **Clean Architecture 四層，依賴方向由測試保護** ——
   `Api → Infrastructure → Application → Domain`，Domain 不知道 AI 與 EF Core 的存在。
   這條邊界由 12 個架構測試把關，不是靠自律。
2. **十二個工具的 tool-use 迴圈** —— 工具契約、錯誤碼、稽核 log 都有明確設計；
   LLM 供應商被抽象在 `ILlmClient` 後面，換一家只要換一個類別。
3. **本機 RAG（十二個工具的最後一個）** —— Ollama embedding + SQLite 向量檢索，
   回傳的是**段落與來源引用**，不是「答案」，因為答案從哪來必須追得到。
4. **一次真實的模型選型事故，變成了 CI 的一道把關** ——
   第一版預設 `nomic-embed-text`，全部測試綠燈，裝上真模型才發現：
   無關查詢（「今天天氣如何」）的相似度比真正相關的查詢還高，
   **沒有任何門檻值能分開兩者，防幻覺的第一道防線形同不存在**。
   換成 `bge-m3` 之後，這件事被寫成一個獨立的 CI job（接真實 Ollama，不准 skip）。
5. **一條完整的 ML pipeline，而且誠實**（`docs/ml-risk-prediction-module-plan-v1.md`）——
   模擬歷史資料 → 清理 → 特徵工程 → logistic regression → ONNX 匯入 .NET 推論，
   與既有的規則式判斷並存對照。訓練資料是模擬的這件事寫在文件第一行，
   不藏在附註裡；閾值 0.26 是依「漏抓比誤報代價高」選出來的，不是預設的 0.5。
6. **每條防線都做過反向驗證** —— 把防線拔掉、確認測試會紅，再還原。
   沒有紅過的測試等於沒有測試。

## 量化成果

| | |
|---|---|
| 測試 | 405 個（本機有 Ollama 時 435），0 警告，CI 兩個 job 全綠 |
| AI 工具 | 12 個（11 唯讀 + 1 寫入建議），背後 10 個 Application 服務 |
| 架構邊界 | 12 個架構測試守住分層依賴 |
| 文件 | AI 助理規劃 v1 → v2 → v3 逐條對帳，另有 RAG 與 ML 兩份獨立的決策紀錄 |

## 示範問答

```bash
curl -X POST http://localhost:5199/api/ai-assistant/ask \
  -H 'Content-Type: application/json' \
  -d '{"question":"這週有哪些工單有延遲風險？缺料的話幫我列建議採購清單。"}'
```

一次 HTTP 請求內部跑多輪 LLM ↔ 工具往返：
LLM 要求 `list_work_orders_at_risk()` → 後端回真實工單 →
LLM 要求 `run_mrp_shortage_analysis()` → 後端回「面板淨缺 120 片、建議下單 150 片」→
LLM 組成一段自然語言。**中間每個數字都來自後端，而且都被測試釘住。**

## 誠實的部分

- `AnthropicLlmClient` 尚未對真實 Anthropic API 打過請求（開發機沒有金鑰）；
  送出的 HTTP 請求已用假伺服器逐欄檢查，接正式端點的測試也寫好了，只差跑一次。
- **ML 延遲風險模型是用模擬資料訓練的**，展示資料的特徵還落在訓練分布之外 ——
  後者現在會在 API 回應裡明講（`outOfDistributionFeatures`），而不是只寫在文件裡。
  它證明的是「這條 pipeline 接起來了、每個決策講得清楚」，不是「模型對真實產線有效」。
- BOM 展開是逐階查詢、RAG 是 brute-force 全表掃描 —— 這兩個在目前資料規模下
  都不構成問題，而「什麼時候該優化、什麼時候不該」本身就是判斷的一部分。
- 完整的已知限制清單在 [README 的「尚未處理」](../README.md#尚未處理)。

## 去哪裡看

- **五十秒的操作實錄**（每個數字都是真跑的）—— [README 最上方的影片](../README.md)
- **三十秒跑起來、實際輸出、設計問答** —— [`docs/demo-and-design-notes.md`](demo-and-design-notes.md)
- **架構決策與踩過的坑** —— [`README.md`](../README.md)
- **工具契約、庫存計算基準、防幻覺機制** —— [`docs/ai-assistant-module-plan-v2.md`](ai-assistant-module-plan-v2.md)
- **RAG 的範疇、資料流、七個決策點** —— [`docs/rag-module-plan-v1.md`](rag-module-plan-v1.md)
- **ML 模組的資料、特徵、閾值與 skew 防線** —— [`docs/ml-risk-prediction-module-plan-v1.md`](ml-risk-prediction-module-plan-v1.md)
- **怎麼把展示站部署上線** —— [`deploy/README.md`](../deploy/README.md)
