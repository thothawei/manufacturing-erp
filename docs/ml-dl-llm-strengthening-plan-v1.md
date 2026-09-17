# ML / DL / LLM 能力強化規劃 v1（2026-09-16）

## 這份文件是什麼

外部草案 `erp-ml-portfolio-plan.md`（三層式：Pandas EDA → sklearn 分類 → FastAPI 部署）
拿來對帳之後的**重寫版**。原草案寫於這個專案的 ML 模組動工之前，它規劃的三層
現在有八成已經完成，照著做等於重做一次已經做過的事。

這份文件先做對帳（第 1 節），指出剩下的真正缺口（第 2 節），
再把後續工作重新排成五個階段（第 3 節起）。

**新增的判斷立場**：`portfolio-strengthening-plan-v1.md` 當初寫「不要為了對齊 JD 硬做
深度學習」。這條在**表格資料**上仍然成立，不推翻；但它被誤讀成「這個專案不做 DL」。
修正後的立場是：

> DL 只做在 DL 有結構性理由贏的地方（文字、排序），
> 而且**一律附 baseline 對照，允許結論是「DL 沒贏」**。

1200 筆 7 個特徵的工單表格上，神經網路贏不了 logistic regression，做了是演戲；
但「客訴文本分類」與「檢索結果重排序」這兩件事，線性模型與詞袋法本來就有上限，
DL 在那裡不是裝飾品。同一條原則套到 LLM：SFT 要做，但要做成
「小模型在窄任務上逼近大模型」的可量測對照，不是丟一份資料微調完宣稱有效。

---

## 1. 對帳：原草案三層 vs 專案現況

| 原草案項目 | 現況 | 說明 |
|---|---|---|
| 第一層 EDA / 資料清洗文件 | **已完成，形式不同** | 沒有 notebook，決策寫在 `ml-risk-prediction-module-plan-v1.md` 第 4-5 節：缺值為何用中位數不加旗標、離群值為何 winsorize 不刪樣本，每條都有理由 |
| 合成資料產生器 | **已完成** | `tools/Erp.MlDataGen` + `HistoricalWorkOrderGenerator`，1200 筆、固定種子 20260913、刻意注入 5% 標籤翻轉與缺值/離群值 |
| 第二層 sklearn 分類模型 | **已完成** | logistic regression + StandardScaler pipeline，ROC AUC 0.7386、AP 0.6997 |
| 兩種模型比較 | **刻意只做一個** | 理由寫在規劃第 7 節：這個規模下 boosting 換來的表現不值得失去可解釋性 |
| 閾值與評估指標 | **已完成且超出草案** | 閾值 0.26 是掃出來的（recall ≥ 0.85 下 precision 最佳），不是預設 0.5；代價不對稱的理由有寫 |
| model card | **等價物已存在** | `docs/ml-risk-prediction-module-plan-v1.md` 就是模型卡的加長版，含已知限制六條 |
| 第三層 FastAPI + Docker + C# HttpClient | **刻意不做，改用 ONNX** | 推論走 `Microsoft.ML.OnnxRuntime` 進程內載入。少一個要顧的服務、少一次網路往返、clone 下來跑測試不必裝 Python |
| C# 端整合 | **已完成** | `IDelayRiskModel` port + `OnnxDelayRiskModel`，AI 工具 `predict_work_order_delay_risk`，端點 `/api/work-orders/{no}/delay-risk`，規則式與模型式並陳 |
| 架構圖文件 | **已完成** | README 專案結構 + 各模組規劃文件 |
| MLflow 實驗追蹤（草案選做） | **已完成 2026-09-17** | 見第 3 節 S5 |
| model drift 監控（草案選做） | **已完成 2026-09-17（PSI）** | 見第 3 節 S5 |
| 物料需求量預測（草案兩個題目之一） | **已完成 2026-09-17** | 見第 3 節 S6 |

草案還列了幾件事，專案也已經獨立完成：多輪對話記憶（`ConversationMemoryTests`）、
prompt injection 對抗測試（`PromptInjectionResilienceTests`）、
帶人工確認的可寫入工具（`PurchaseApprovalFlowTests`）、
檢索評測集擴充到 30 組並加上排序指標（`RetrievalEvaluationSet`）。

**結論：原草案能給的東西已經被榨乾了。後續規劃要從它沒寫到的地方開始。**

---

## 2. 真正的缺口

盤點下來只有三塊是真的空的，其餘都是既有東西的深化：

### 缺口 A：深度學習，一行都沒有

現有 ML 是 logistic regression。履歷寫「ML/DL」時，DL 那半個字現在撐不住。
缺的不是「跑過 PyTorch」，是「知道什麼時候該用 DL、什麼時候不該」的證據 ——
而後者要有對照組才說得出口。

### 缺口 B：LLM 只有「用」，沒有「訓練」

現有 LLM 能力全在推論側：tool-use 迴圈、RAG、injection 防禦、多輪記憶。
JD 明寫的 SFT / 微調完全沒有。這塊做不做得成，決定履歷能不能寫
「LLM 微調」而不只是「LLM 應用整合」。

### 缺口 C：模型生命週期（MLOps）

訓練是一次性腳本，沒有實驗追蹤、沒有版本化、沒有漂移偵測、沒有重訓觸發設計。
`ml-risk-prediction-module-plan-v1.md` 第 10 節自己把這些列為已知限制，
現在是把其中可做的部分關掉的時候。

另外有兩條既有限制屬於「小工作量、直接關掉」：模型未校準（calibration）、
沒有分布外（OOD）輸入檢查。列進 S0。

---

## 3. 階段規劃

順序是刻意的：先用最小成本關掉既有限制（S0），再把 DL 放進**已經有量測基準**
的地方（S1，檢索評測集現成），接著才做需要新標註資料的 DL（S2）與最貴的 SFT（S3）。

| 階段 | 主題 | 技能標籤 | 估時 | 相依 |
|---|---|---|---|---|
| ~~S0~~ | ~~機率校準 + OOD 偵測~~ | ML 工程 | **已完成 2026-09-16** | — |
| S1 | Cross-encoder reranker 進 RAG | **DL** / NLP / 檢索 | 3-4 天 | 無 |
| S2 | 客訴文本多標籤分類（微調 BERT） | **DL** / NLP / 特徵工程 | 4-5 天 | 無 |
| S3 | LoRA SFT：小模型做工具呼叫 | **LLM 微調 / SFT** | 5-7 天 | S4 的評測集先有雛形較好 |
| ~~S4~~ | ~~Agent 端到端評測 harness~~ | LLM 評測 / 可靠性 | **已完成 2026-09-17** | 無 |
| ~~S5~~ | ~~MLflow + 漂移監控 + 模型註冊~~ | MLOps | **已完成 2026-09-17（模型註冊、MLflow、PSI 漂移偵測）** | 無 |
| ~~S6（選做）~~ | ~~物料需求時間序列預測~~ | 時間序列 / DL 對照 | **已完成 2026-09-17** | 無 |

總計約 4 週（不含 S6）。每個階段可獨立 commit、獨立寫進履歷，中途停在任何一階
都不會留下半成品 —— 這是排序的硬約束。

### 全域規則（每個階段都適用，違反就是這份規劃失敗）

1. **每個 DL 模型都要有非 DL baseline 對照**，數字並列在同一張表。DL 沒贏就寫沒贏。
2. **合成資料一律在文件最上方標明**，比照 ML 模組現有做法。
3. **推論端不引入 Python 服務**：模型一律匯出 ONNX，由 .NET 載入；
   沿用 `IDelayRiskModel` / `IEmbeddingClient` 的 port + 可選模組模式
   （模型載不起來 → `IsAvailable = false`，如實說沒有模型，**不回一個看起來像結果的預設值**）。
4. **每個模型都要有黃金樣本測試**：訓練時存下 N 組輸入與 Python 端輸出，
   .NET 端驗算出同一個數字（誤差 < 1e-6）。這抓的是不會報錯的失敗（欄位順序、取錯輸出欄）。
5. **每條新測試都要做反向驗證**：把防線拔掉，測試必須紅。不紅的測試等於沒有。
6. **CI 必須維持綠燈、0 警告**，新的重量級測試比照 Ollama 那組用條件 skip。

---

## S0：機率校準 + 分布外偵測 —— 已完成（2026-09-16）

完整紀錄在 [`ml-risk-prediction-module-plan-v1.md`](ml-risk-prediction-module-plan-v1.md) 第 10 節。摘要：

- **校準：評估過，結論是不採用。** Platt 與 isotonic 的 ΔBrier 95% bootstrap 區間都跨 0
  （[−0.0018, +0.0009] 與 [−0.00123, +0.00567]），300 筆測試資料分不出差別。
  判準先定好才跑，不是看了結果再挑說法。改成由 `IDelayRiskModel.IsCalibrated` 對外講明
  「機率只保證排序」，API 與 AI 工具描述同步。
- **OOD：做了。** 訓練分布的 p1/p99 進 metadata，推論時比對，
  超界的特徵與訓練範圍一起回報在 `outOfDistributionFeatures`。
  第一個被標出來的就是展示資料自己（`weekly_load_ratio` 0.125，訓練下界 0.5088）。
- **模型權重完全沒動** —— 重跑訓練後 ONNX 位元相同，變的只有 metadata 與模型對自己的說明。
- 新增 8 個測試（405 → 本機有 Ollama 時 435），四條新防線逐一反向驗證過。
- **意外收穫**：float32 的特徵向量與 double 的邊界直接比大小，
  會把訓練集裡「值剛好等於邊界」的樣本誤判成分布外。是反向對照的那條測試抓到的。
  同樣的陷阱在 S1 的 rerank 分數門檻、S2 的多標籤閾值上都會再出現一次。

---

## S1：Cross-encoder reranker 進 RAG（3-4 天）— 第一個 DL

**為什麼是這個先做**：現有 RAG 是 bge-m3 雙塔 embedding + 暴力 cosine，
而評測集（30 組標記 query、含 MRR/NDCG）**已經在了**。
也就是說改善幅度當場可量測，不必先建量尺。雙塔模型把 query 與段落各自壓成一個向量，
交互資訊在編碼時就丟了；cross-encoder 讓兩者一起進 encoder 做 token 級交互，
這是它在排序上贏的結構性理由 —— 代價是不能預先算，只能對候選集算。

**做法**：兩階段檢索。bge-m3 取 top-20 → cross-encoder 重排 → 取 top-5 給 LLM。
模型用 `bge-reranker-v2-m3`（中文可用），匯出 ONNX 由 .NET 載入；
沿用可選模組模式，載不起來就退回單階段檢索並在回應裡說明。

**要報的數字**（同一份 30 組評測集，前後對照）：

| | 單階段（現況） | 兩階段 |
|---|---|---|
| MRR@5 | 基準值 | ? |
| NDCG@5 | 基準值 | ? |
| 不相關 query 的最高分（拒答門檻是否還守得住） | 基準值 | ? |
| p50 / p95 延遲（ms） | 基準值 | ? |

**誠實界線**：30 組 query 的差異可能落在雜訊裡。所以要同時報
「哪幾組的排名真的變好、哪幾組變差」的逐題表，不是只報平均。
若總體沒贏，結論就寫「在這個語料規模上 reranker 不值得那段延遲」，
**並保留開關與數字** —— 這個結論跟贏了一樣能講。

**驗收**：逐題對照表進文件；黃金樣本測試（ONNX vs Python 分數一致）；
拔掉 reranker 該測試紅；CI 綠。

---

## S2：客訴文本多標籤分類，微調中文 BERT（4-5 天）— 第二個 DL

**為什麼 DL 在這裡站得住**：分類對象是自然語言短文，
詞袋法處理不了「顏色偏黃」與「色溫不對」是同一件事，
而預訓練 encoder 的語意表示正是為此而生。這裡的 DL 不是為了看起來像 DL。

**任務**：客訴文本 → 多標籤（品質-外觀 / 品質-功能 / 交期 / 包裝 / 服務態度 / 其他），
一則客訴可命中多個標籤。

**資料**：比照現有做法在 C# 側寫生成器（`tools/Erp.MlDataGen` 擴充），
產 1500 筆合成客訴，固定種子；刻意注入現實雜訊：同義改寫、錯別字、
5% 標籤雜訊、類別長尾（服務態度只佔 4%）。長尾是重點 —— macro-F1 才有意義。

**三方對照，這是本階段的核心產出**：

| 方法 | 訓練成本 | 推論成本 | macro-F1 | 長尾類別 F1 |
|---|---|---|---|---|
| TF-IDF + LinearSVC（baseline） | 秒級 | 極低 | ? | ? |
| 微調 `hfl/chinese-macbert-base` | 分鐘級（CPU/MPS 可跑） | 中 | ? | ? |
| LLM zero-shot（既有 OmniRoute 通道） | 0 | 高（每則都要錢） | ? | ? |

**這張表本身就是面試素材**：三種方法的成本結構完全不同，
「該選哪個」取決於量體與延遲要求，不是誰的 F1 高誰就贏。
預期結果是 BERT 在長尾類別上明顯贏 TF-IDF，而 LLM zero-shot 在標籤定義模糊時反而不穩 ——
但**預期不算數，要跑出來才寫**。

**落地**：ONNX 匯出 → `IComplaintClassifier` port → AI 工具 `classify_complaint`
（輸入一則客訴文字，回標籤與信心值）；分類結果可接回品質統計，
讓 RAG 語料裡的客訴紀錄有結構化標籤可篩。

**驗收**：三方對照表 + 混淆矩陣進文件；黃金樣本測試；
閾值選擇的理由寫清楚（多標籤要逐標籤選閾值，不是全部用 0.5）；CI 綠。

---

## S3：LoRA SFT — 小模型做工具呼叫（5-7 天）— 缺口 B

**目標**：把「自然語言 → 該呼叫哪個工具、參數是什麼」這個窄任務，
從 4B 級小模型微調出來，對照未微調的自己與大模型。
這是 JD 明寫的 SFT，也是目前履歷唯一缺的 LLM 訓練側經驗。

**資料**：從既有 12 個工具的 schema 出發，程式化生成 instruction → tool_call JSON 對，
每個工具 150-250 組，含改寫、口語、含糊指涉、需要多工具、以及**該拒答的越界問題**
（越界樣本不可少，否則微調完的模型會變得什麼都想呼叫工具）。
大模型輔助改寫 query 可以，但**標籤（該呼叫哪個工具）由生成器決定，不是大模型說了算**。

**性質要講白**：這是自產資料的 distillation + schema 對齊，
不是「用真實生產對話微調」。文件最上方標明，比照 ML 模組的做法。

**訓練**：Qwen3-4B-Instruct + LoRA（rank 16 起跳，實測調），
Mac 走 MLX 或 unsloth；記錄 loss 曲線、LoRA 超參、訓練時長與硬體。

**評測（對照組是重點）**：

| 模型 | 工具選擇正確率 | 參數正確率 | JSON 合法率 | 該拒答時拒答率 | 每次呼叫成本 | p95 延遲 |
|---|---|---|---|---|---|---|
| Qwen3-4B base | ? | ? | ? | ? | 本機 0 元 | ? |
| Qwen3-4B + LoRA | ? | ? | ? | ? | 本機 0 元 | ? |
| Claude（現行） | ? | ? | ? | ? | 有 | ? |

**落地**：微調後的模型掛成 OmniRoute 的一個 provider，
AI 助理可由設定切換；文件寫清楚適用範圍
（成本與資料不出境 vs 準確率落差，以及落差有多大）。

**誠實界線**：小模型大概率在多工具串接與模糊問題上輸給 Claude。
輸了就寫輸多少，並寫出「什麼情境下這個落差可以接受」——
這比宣稱微調完追平大模型可信得多。

**驗收**：三方對照表；評測集與跑法進 repo 可重跑；
切換 provider 後端到端跑通一次並存下紀錄。

---

## S4：Agent 端到端評測 harness —— 已完成（2026-09-17），但跟 S1-S3 一起卡在同一個環境限制

**為什麼是 S4 先做，不是照表訂的 S1**：S1/S2/S3 都要從 huggingface.co（或其鏡像）
抓預訓練權重（`bge-reranker-v2-m3`、`hfl/chinese-macbert-base`、`Qwen3-4B`），
而執行這份規劃的雲端 session 的網路政策把 huggingface.co／hf-mirror.com／modelscope.cn
全部擋在 403（org policy denial，不是暫時性的連線問題，`curl` 對三個網域都拿到
「gateway 403 to CONNECT」）。S4 不需要下載任何模型，純 C#/測試工程，
所以先做這個、把 S1-S3 留給有網路權限跑的環境或下一個 session。

**實作結果**：照這一節的方向做，四個要量測的指標全部做了（工具選擇正確率、數字幻覺率、
拒答正確率、token／成本／延遲），CI 整合方式也照抄 `AnthropicLiveApiTests`
（同一個 `AnthropicLiveFactAttribute`／`ANTHROPIC_REQUIRE_LIVE` 機制，沒有另外開一個
常駐的 CI job——`AnthropicLiveApiTests` 本身也沒有，見下面的落地細節）。
三處跟原規劃不同或補的東西：

- **案例數 44，不是評測數字，是刻意的下限**：原規劃寫 40-60。44 題涵蓋六個維度
  （單工具 × 12 個工具、跨工具、消歧、查無資料、超出範圍），每個工具至少兩題、
  跨工具至少六題——再往上加案例邊際價值遞減，44 題已經能撐住四個指標的統計意義，
  且每題都要真的接一次 Claude、可能還要走多輪工具呼叫，案例數直接等於這組測試的
  真實成本與跑多久，不是越多越好。
- **「期望數字」不是寫死在案例集裡，是現場從工具回傳的 JSON 撈出來的**：
  `AgentEvalCase` 只記錄查詢文字、案例類別、期望被呼叫的工具、要不要拒答，
  不記錄任何數字。原規劃寫「期望數字由既有 API 直接算出當真值」，
  照字面做法是另外呼叫一次 Application Service 算出期望值、再跟模型答案比對——
  但這樣等於維護兩份「正確答案」（案例集裡一份、工具回傳一份），兩者不同步時
  分不出是模型錯還是案例集過期。改成：**這次對話裡模型自己呼叫工具拿到的 JSON，
  就是這次的 ground truth**（工具本身就是「既有 API」，且是真的在這通對話裡現場查的，
  不是預先算好貼上去）。數字幻覈偵測拿答案裡的每個數字去比對「這次對話裡所有工具
  回傳過的文字」，不在裡面的才算幻覺——比對方式是字面子字串，已知的假陽性/假陰性
  方向在 `AgentEvaluationHarness.FindHallucinatedNumbers` 的註解裡寫明。
- **LLM-as-judge 那個維度沒有做**：原規劃寫「回答是否切題」用 judge、
  抽 20 組人工校準一致率。這份 44 題的案例集裡沒有「有沒有離題」這種沒有標準答案的
  題目——四個已實作指標（工具選對、數字不編、該拒答時拒答、token/成本/延遲）
  已經涵蓋所有題目「答得對不對」可以程式化判斷的部分，加一個 judge 維度是為了做而做。
  之後如果案例集擴充到需要判斷「措辭是否得體」「有沒有主動提供有用的追問建議」這類
  沒有標準答案的維度，再補這件事，屆時 20 組人工校準一致率一樣不能省。

**反向驗證怎麼做的**（規劃裡明寫的驗收標準）：`AiAssistantService.AskAsync` 內部固定呼叫
`AssistantScope.ToolsFor(role)`，角色過濾是唯一的注入點，刻意不留後門讓呼叫端夾帶
自訂工具清單。所以反向驗證測試（`反向驗證_工具描述被改壞後工具選擇正確率會下降`）
在測試檔裡本地重建了一份陽春版 tool-use 迴圈（不含對話記憶、角色過濾），
只為了能把 `check_material_sufficiency_for_item` 的說明換成一句誤導文字重跑，
證明正確率會下降。這是刻意的、僅供這個測試用的重複，不是要取代 `AiAssistantService`。

**離線那一半**：`AgentEvaluationHarnessTests`（8 個測試，不需金鑰）把計分邏輯本身釘住——
幻覺偵測抓不抓得到編出來的數字、工具選錯時工具選擇正確率會不會變成 0、
`Aggregate` 的三個比率算得對不對、`EstimateCostUsd` 對已知/未知模型的行為、
`BuildReport` 產出的報表含不含該有的區塊。這一半是這個 harness 唯一在這個環境裡
真的跑過、真的綠燈的部分。

**誠實的邊界，跟 Phase 0 是同一筆帳**：這個 session 沒有 Anthropic 金鑰，
`AgentEvaluationTests`（含完整 44 題評測集、反向驗證）在這裡是 skip 狀態，
`docs/verification/` 底下還沒有 `agent-eval-*.md`。工具選擇正確率、數字幻覺率、
拒答正確率、token/成本/延遲——**這些數字目前一個都沒有真的跑出來過**，
不能講「應該會過」。設好金鑰跑一次 `dotnet test tests/Erp.Infrastructure.Tests
--filter "FullyQualifiedName~AgentEvaluationTests"` 就會補上。

成本估算用的官方牌價（`AgentEvaluationHarness` 裡的 `Pricing` 表）是 2026-09-17
讀自 platform.claude.com/docs/en/about-claude/pricing 的即時資料，
不是訓練資料裡的舊數字；牌價會變，程式碼裡的註解已經標明來源與日期。

**原本的規劃**

現有測試驗的是「工具契約、錯誤路徑、LLM 不可竄改數字」，
沒有一組**量化**的「這個 agent 答得對不對」。S3 需要這把尺，S4 先把尺造出來。

- 40-60 組端到端案例：query → 期望工具序列 + 期望數字（數字由既有 API 直接算出當真值）。
- 量測：工具選擇正確率、**數字幻覺率**（答案裡出現工具沒回傳過的數字）、
  拒答正確率、平均 token 數、成本、p50/p95 延遲。
- 數字幻覺率用程式比對，不用 LLM-as-judge；只有「回答是否切題」這種
  沒有標準答案的維度才用 judge，而且要抽 20 組人工校準 judge 的一致率並把該數字寫出來
  （沒校準過的 judge 分數不能當證據）。
- 進 CI，需金鑰時條件 skip，比照 `AnthropicLiveApiTests`。

**驗收**：報表可一鍵產生；故意把一個工具描述改壞 → 正確率該掉（反向驗證）。

---

## S5：MLflow + 漂移監控 + 模型註冊 —— 已完成（2026-09-17）

**先做了模型註冊那一小塊（2026-09-17）**，理由跟 S4 一樣：不需要下載任何預訓練權重，
純 C# 工程，跟 S1-S3 被同一個網路限制卡住的處境無關。

- `OnnxDelayRiskModel` 現在會比對 metadata 宣告的特徵順序跟 `WorkOrderDelayFeatures.FeatureNames`，
  對不上就把 `IsAvailable` 判定為 false（連同 `PredictDelayProbability` 一起拒絕執行），
  不是照跑一個把數值餵進錯欄位、外觀正常但語意錯誤的機率。跟分布外檢查是同一種「不報錯的
  失敗」，處理方式也一樣：停用，而不是照跑。反向驗證：`DelayRiskModelTests` 裡把 metadata
  的一個特徵名稱換掉，模型會停用；換回來則不會。同一道防線也套用到 S6 的
  `OnnxMaterialDemandForecastModel`。
- 新增 `/api/ml/model-health` 端點，把 `available`／`featureSchemaConsistent`／`isCalibrated`／
  `decisionThreshold`／`trainedOn`／`dataSource`／`rowsTotal`／`rocAuc` 曝露出來——
  這就是這一節講的「C# 端啟動時把版本號寫進…健康檢查端點」，只是沒有版本號（見下）。

**後來 S6 做完、有了第二組可比較的模型之後，補上了實驗追蹤（2026-09-17）**：
原本判斷「只有一個模型，沒有第二個實驗可比，等 S1/S2 有模型再接」——但 S6 本身
就產生了三個要互相對照的訓練結果（seasonal naive／GBDT／MLP），這正是 MLflow 比較視圖
的典型情境，不需要等到 S1/S2。做的事：

- `ml/train.py` 與 `ml/train_demand_forecast.py` 都接了 MLflow（本機 file store
  `ml/mlruns/`，不架伺服器，`.gitignore` 排除——重跑訓練腳本會重新產生，不需要進 repo）。
  記參數（random_state、特徵數、切分方式等）、指標（AUC/MAPE/MAE/RMSE 等，依模型而定）、
  是否為贏家／是否為部署對象的 tag；部署的模型另外把 ONNX 與 metadata JSON 存成 artifact。
- `ml/train_demand_forecast.py` 把三個方法各記成同一個 experiment 底下的一個 run，
  UI 的「比較執行」表格天生就是為了這種情境設計的。截圖（`docs/images/
  mlflow-demand-forecast-comparison.png`）貼在
  [`docs/material-demand-forecast-plan-v1.md`](material-demand-forecast-plan-v1.md)。
- **驗證過重跑訓練腳本不會動到模型本身**：重新訓練後 `work-order-delay-model.json`
  只有 `trained_on` 日期變了，`material-demand-forecast-model.json` 完全沒變、
  `.onnx` 只有 onnxmltools 隨機產生的圖形名稱（UUID）不同，golden sample 數字
  逐位元相同——MLflow 只是多觀測這次訓練，不影響訓練本身的任何計算。

**後來又補上了漂移偵測（PSI，2026-09-17）**：先加了 `IRecentPredictionLog<T>`——
一個有界環狀緩衝區（容量 200，跟 `InMemoryConversationStore` 是同一種模式），兩個模型的
預測服務每次算特徵都會記一筆，不管模型當下可不可用（漂移偵測要看的是線上輸入本身，
跟模型能不能推論是兩件事）。有了這層記錄，PSI 才有東西可以比：

- 訓練腳本（`ml/train.py`、`ml/train_demand_forecast.py`）新增 `drift_profile()`，
  對每個特徵算 10 等分位數邊界，存進 metadata JSON。**分箱邊界不能假設均勻**：
  第一版假設每箱剛好 1/10，被 `item_overdue_rate`（真實訓練資料只有 5 種值）
  的測試打臉——9 個分位數切點會切出重複邊界，實際比例是
  `[0.189, 0.18, 0.19, 0.209, 0.232, 0.0]`（6 個去重後的箱）而不是均勻的
  10 等分。改成先去重邊界、再對訓練集重新算一次真正的比例，metadata 存的是
  這個實際比例，不是假設出來的。這是寫測試時撞到的真實案例，不是預想的邊界情況。
- `PsiCalculator`（`Erp.Application.Ml`）用去重過的邊界、實際訓練比例、與最近觀察值
  算 PSI，門檻是業界慣用的經驗法則（&lt; 0.1 沒有顯著變化、0.1~0.2 中度飄移、
  ≥ 0.2 顯著飄移），不是統計顯著性檢定。`IDelayRiskModel`／`IMaterialDemandForecastModel`
  都新增 `ComputeDrift`，沒有 metadata（或沒有 drift bin edges）時回 `null`——
  跟分布外檢查一樣，「無法判斷」不等於「沒有飄移」。
- `/api/ml/model-health` 與新增的 `/api/ml/demand-forecast-health` 都曝露 `drift`
  區塊（`recentSampleCount`／`minSampleSize`／`perFeaturePsi`／`significantDriftFeatures`／
  `note`）。樣本數不到 30 筆時 `drift.available` 為 `false`——30 不是統計推導出來的
  臨界值，是「先求不要在樣本太少時誤報飄移」的工程判斷。
- 反向驗證：測試裡用「重現訓練時的實際分箱比例」建構觀察值，PSI 應該接近 0；
  用「所有觀察值都釘在同一個極端值」建構觀察值，PSI 應該超過顯著門檻。兩個模型
  各有一份，見 `DelayRiskModelTests`／`MaterialDemandForecastModelTests`。

**沒做、而且刻意沒做的**：

- **沒有版本號／訓練資料雜湊**。metadata JSON 目前沒有這兩個欄位，要加就得改
  訓練腳本並重新訓練一次（即使模型權重不變，metadata 格式改了也得重新產生這個檔案，
  不能手動編輯——那不是「訓練當下量出來的數字」了）。留給下一次真的改動特徵/模型時
  （例如做 S1/S2）一起補，不單獨為了加兩個欄位跑一次訓練——MLflow 的 run ID 本身
  已經是一種輕量的版本標記，兩者不是互斥的，只是欄位優先權排在後面。
- **重訓觸發設計**：跟 `ml-risk-prediction-module-plan-v1.md` 第 11 節寫的一致，
  標籤延遲讓自動重訓在這個規模是假的，PSI 偵測到飄移之後要不要重訓、怎麼重訓，
  是人的判斷，沒有新東西可補。

**原本的規劃**

- **實驗追蹤**：`ml/train.py`（以及 S1/S2 的訓練腳本）接 MLflow，
  記參數、指標、資料版本雜湊、模型檔。本機 file store 即可，不架伺服器。
  貼一張 UI 比較截圖進文件 —— 這是面試官一眼能看懂的東西。
- **模型註冊**：現有 metadata JSON 升級成含版本號、訓練資料雜湊、
  評估數字、訓練時間；C# 端啟動時把版本號寫進 log 與健康檢查端點。
  **模型檔與程式碼版本對不上時要能當場看出來** —— 這是實務上最常見的事故。
- **漂移偵測**：寫一支比對「訓練分布 vs 近期線上輸入分布」的腳本，
  逐特徵算 PSI，超過門檻列為警示；端點 `/api/ml/model-health` 回模型版本與最近一次漂移檢查結果。
- **重訓觸發設計**：標籤延遲（工單要完工才知道有沒有延遲）讓自動重訓在這個專案是假的，
  所以**設計寫清楚、實作只做到偵測與告警**，並寫明為什麼停在這裡。
  誠實界線與 ML 規劃第 2 節一致。

---

## S6（選做）：物料需求時間序列預測 —— 已完成（2026-09-17）

**為什麼選做項目先做了，S1/S2 還沒**：跟 S4/S5 那一小塊一樣，S6 不需要下載任何
預訓練權重（GBDT 從零訓練、MLPRegressor 用 scikit-learn 內建，不碰 huggingface），
純 Python/C# 工程，不受這個 session 的網路政策限制。排序上原本 S1/S2 因為
「同時補上 DL 與 NLP，投報率更高」排在 S6 前面，但那個判斷的前提是三者都做得了——
現在只有 S6 做得了，繼續空等不如先把它做完。

**實作結果**：三方對照真的做了，而且結果跟原規劃的預期不完全一樣——
GBDT 贏（整體 MAPE 6.63%），seasonal naive 其次（9.88%），
小型 DL（MLPRegressor）明顯最差（19.92%）。原規劃寫的是「seasonal naive 打敗 DL
是常態」，實測是「DL 確實輸了，但輸給 GBDT，不是輸給 seasonal naive」——
方向沒錯（DL 不是萬靈丹），細節跟猜測不一樣，這正是「先猜的結論不算數，
跑出來的才算數」的具體例子。完整決策紀錄在
[`docs/material-demand-forecast-plan-v1.md`](material-demand-forecast-plan-v1.md)。

三件跟原規劃不同、或做下去才長出來的事：

- **DL 的角色是 MLPRegressor，不是 N-BEATS/1D-CNN**：這個 repo 的訓練工具鏈只有
  scikit-learn，沒有 PyTorch。理由與取捨寫在 material-demand-forecast-plan-v1.md 第 6 節，
  不是省事的藉口——3 個品項、156 週的資料量下，換一個更重的框架不會改變
  「這個規模下 DL 不一定贏」的結論方向。
- **沒有接回 MRP**：原規劃寫「換成預測需求就有了『預測 → 決策』的完整鏈路」，
  但這是一個會影響 `MrpCalculationService` 既有行為與既有測試的重大變更，
  不是選做模組該附帶做的事，範疇界定裡明講了。
- **端點設計誠實反映一個架構缺口**：這個系統目前沒有任何地方持久化歷史週別需求，
  所以 `/api/ml/demand-forecast` 是 POST、特徵由呼叫端提供，不是伺服器從資料庫查的。
  也因為同一個理由，**沒有掛成第十三個 AI 工具**——LLM 沒有管道自己生出一組 lag
  特徵，硬掛上去只會是一個問不出來、也答不出來的工具。

**原本的規劃**

原草案的第二個題目，專案沒做。價值在於它直接接得上既有 MRP：
現在的 MRP 用固定需求算採購建議，換成預測需求就有了「預測 → 決策」的完整鏈路。

做的話同樣要三方對照：seasonal naive（baseline，絕對不能省）、
GBDT with lag features、小型 DL（N-BEATS 或 1D-CNN）。
**小樣本時間序列上 seasonal naive 打敗 DL 是常態**，那個結論本身很值得寫。

排在最後是因為：它證明的技能點與 S1/S2 重疊（都是「選模型並誠實對照」），
而 S1/S2 同時補上 DL 與 NLP，投報率更高。

---

## 履歷可寫的句子（依實際跑出的數字調整，不得先寫後補）

完成 S1-S3 後可寫：

> 於自建 ERP 中實作三個機器學習模組並全部以 ONNX 整合進 ASP.NET Core：
> (1) 工單延遲風險二元分類（logistic regression，ROC AUC 0.74，依代價不對稱選定閾值 0.26）；
> (2) 以 cross-encoder reranker 改良 RAG 兩階段檢索，於 30 組標記評測集上 MRR@5 由 X 提升至 Y；
> (3) 微調中文 BERT 做客訴多標籤分類，macro-F1 相對 TF-IDF baseline 提升 Z。
> 另以 LoRA 微調 4B 小模型執行工具呼叫任務，工具選擇正確率達 N%，
> 並與大模型在成本／延遲／準確率三維度做完整對照。

已經可以寫（S6 已完成，數字是 2026-09-17 實測跑出來的）：

> 以時間序列三方對照（seasonal naive／LightGBM／MLPRegressor）預測物料週別需求，
> GBDT 於 20 週測試集上 MAPE 6.63%，優於 seasonal naive 基準線（9.88%）與
> 小型神經網路對照組（19.92%），模型以 ONNX 整合進 ASP.NET Core。

每個數字都必須是實際跑出來的。**沒跑出來的就不寫，寫了就要能當場重跑。**

---

## 下一步

S0、S4、S6 已完成（2026-09-16、2026-09-17、2026-09-17），
S5 的模型註冊、實驗追蹤、PSI 漂移偵測三塊都完成了（2026-09-17，見該節）。
S1/S2/S3 都卡在同一個環境限制——
執行這份規劃的雲端 session 連不上 huggingface.co／hf-mirror.com／modelscope.cn
（org policy 403，見 S4 開頭的說明），沒有預訓練權重就做不下去，
這個限制不影響 S5/S6（都沒有預訓練權重需求）。
下一步：換一個網路政策允許連到 HuggingFace（或有現成鏡像可用）的環境接手 S1，
或者由你決定要不要調整這個 session 的網路權限。S4 的離線那一半
（`AgentEvaluationHarnessTests`）已經綠燈，但 `AgentEvaluationTests` 那一半
（真的接 Claude 跑 44 題、量出真實數字）跟 Phase 0 一樣卡在沒有 Anthropic 金鑰——
兩件事一起補會比較有效率（同一個 `dotnet test`、同一份 `docs/verification/`）。
每階段結束時：更新本文件對應章節為實作紀錄（比照 v1→v2→v3 的做法）、
跑完 CI 四步、commit。
