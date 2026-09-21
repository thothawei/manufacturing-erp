# 未收尾的 worktree 改動存檔

2026-09-21 從 `.claude/worktrees/` 底下四個殘留 agent worktree 搶救出來的改動，
搶救後即刪除 worktree。對應 [`docs/ml-dl-llm-strengthening-plan-v1.md`](../ml-dl-llm-strengthening-plan-v1.md)
的 S2/S3/S5，但都還沒到能收斂進主線的程度，先存 patch 保留，之後要撿再套用。

- `s2-complaint-generator.patch` —— S2 客訴多標籤分類的資料生成器
  （`ComplaintGenerator.cs` + 測試）。`dotnet build` 過、9/9 測試通過。
  缺：實際微調 BERT 的訓練腳本與評估。
- `s3-sft-tool-dataset.patch` —— S3 LoRA SFT 的工具呼叫資料集
  （`Erp.ToolCatalogExport` 匯出工具 + 2289 筆 train/valid/test split）。
  `dotnet build` 過。缺：實際跑一次 LoRA 訓練（無 adapter 產出）。
- `s5-mlflow-inference-recorder-BROKEN-BUILD.patch` —— S5 推論輸入紀錄器
  （`IInferenceInputRecorder` / `BoundedInferenceInputRecorder`）。
  **`dotnet build` 失敗**：建構子加了新參數，但兩處測試呼叫點沒跟著改。

套用方式（在乾淨的 main 上）：

```bash
git apply docs/wip-patches/<file>.patch
```
