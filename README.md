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

## 目前進度

| 階段 | 狀態 |
|---|---|
| Phase 1 — AI 工具背後的查詢／計算服務 | Application 層邏輯與單元測試完成，尚未接資料庫 |
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

## 尚未處理

- Infrastructure 層還沒有 EF Core 實作，Repository 介面目前只有測試用的 in-memory 版本。
- MRP 假設工單需求尚未反映在 `ReservedQty`；工單若已實際發料會高估需求量（方向保守）。
- AI 助理為單輪問答，無對話上下文。
