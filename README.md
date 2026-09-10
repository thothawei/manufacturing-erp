# 製造業 ERP 系統

Clean Architecture 分層的製造業 ERP，含一個以 tool-use 驅動的 AI 助理模組（規劃中）。

## 專案結構

```
src/
  Erp.Domain          實體與領域規則，不依賴任何外部套件
  Erp.Application     使用案例服務 + Repository 介面（port）
  Erp.Infrastructure  Persistence 與（Phase 2 起）Infrastructure.AI
  Erp.Api             HTTP 端點
tests/
  Erp.Application.Tests   Application 層單元測試（以 in-memory 假 Repository 驅動）
docs/
  ai-assistant-module-plan-v2.md   AI 助理模組規劃（v2）
```

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
| Phase 1 — AI 工具背後的查詢／計算服務 | 完成，含 EF Core 資料層、種子資料與 58 個測試 |
| Phase 2 — Infrastructure.AI 與 tool-use 迴圈 | 未開始 |
| Phase 3 — 補完 8 個工具與防幻覺測試 | 未開始 |
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
- **AI 助理為單輪問答**，無對話上下文。

## SQLite 的一個限制

EF Core 把 `decimal` 存成 TEXT，資料庫層無法正確比較或排序數量欄位。
因此所有對數量的比較、加總、排序都必須在載入到記憶體之後才做，
查詢條件只用字串、日期與列舉 —— 各 Repository 都遵守這條，改動時要留意。
