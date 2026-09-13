"""工單延遲風險模型：訓練、評估、匯出 ONNX。

執行前先產生訓練資料（特徵定義的唯一真相在 C# 那邊）：
    dotnet run --project tools/Erp.MlDataGen

然後：
    python3 -m venv ml/.venv && ml/.venv/bin/pip install -r ml/requirements.txt
    ml/.venv/bin/python ml/train.py

輸出：
    src/Erp.Infrastructure/Ml/work-order-delay-model.onnx
    src/Erp.Infrastructure/Ml/work-order-delay-model.json（訓練當下的評估數據）

**資料是模擬的。** 生成規則是我自己寫的（見 HistoricalWorkOrderGenerator），
所以模型學得回那條規則幾乎是必然的 —— 那證明的是 pipeline 接起來了，
不是這個模型對真實產線有效。這件事寫在文件最前面，不藏在附註裡。
"""

import json
from datetime import date
from pathlib import Path

import numpy as np
import pandas as pd
from skl2onnx import to_onnx
from skl2onnx.common.data_types import FloatTensorType
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import (average_precision_score, confusion_matrix,
                             precision_recall_fscore_support, roc_auc_score)
from sklearn.model_selection import train_test_split
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import StandardScaler

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "ml" / "data" / "work-order-history.csv"
OUT_DIR = ROOT / "src" / "Erp.Infrastructure" / "Ml"

# 順序必須與 WorkOrderDelayFeatures.FeatureNames 一致。
# ONNX 吃的是沒有欄位名稱的張量，順序錯了它會照算不誤，只是算出來毫無意義。
FEATURES = [
    "material_readiness",
    "days_until_due",
    "max_lead_time_days",
    "progress_ratio",
    "item_overdue_rate",
    "weekly_load_ratio",
    "bom_component_count",
]

RANDOM_STATE = 20260913

# 漏抓一張會延遲的工單（false negative），代價是客戶端的交期跳票；
# 誤報（false positive）的代價只是生管多看一眼。兩者不對稱，
# 所以決策閾值不用預設的 0.5，改成「在 recall 至少這麼高的前提下，precision 最好的那個」。
TARGET_RECALL = 0.85


def clean(df: pd.DataFrame) -> tuple[pd.DataFrame, dict]:
    """資料清理。每個決定都要說得出為什麼選這個、而不是另一個。"""
    notes = {}

    # --- 缺值 ---
    # 齊套率缺 8.9%、當週負載缺 5.4%。兩個都用「中位數補值」而不是刪除樣本：
    # 9% 的樣本量不小，刪掉會連帶丟掉那些列其他欄位的資訊。
    #
    # 那為什麼不加一個 is_missing 旗標（通常是比補值更好的做法）？
    # 因為**線上推論時這兩個欄位一定算得出來** —— 系統有 BOM、庫存與工單，
    # 齊套率不會缺。缺值只存在於歷史資料（舊系統沒有這個欄位）。
    # 加旗標等於製造一個線上永遠是 0 的特徵，模型會學到一個上線後不存在的訊號。
    for column in ["material_readiness", "weekly_load_ratio"]:
        median = df[column].median()
        missing = int(df[column].isna().sum())
        df[column] = df[column].fillna(median)
        notes[f"{column}_missing_filled"] = missing
        notes[f"{column}_fill_value"] = round(float(median), 4)

    # --- 離群值 ---
    # 當週負載有 1.5% 的極端值（盤點當天一次開出整月工單造成的假尖峰）。
    # 用 IQR 上界夾住（winsorize）而不是刪除：那些列的其他欄位仍然是有效資料，
    # 而且「負載很高」本身是真的，只是高得不合理。
    q1, q3 = df["weekly_load_ratio"].quantile([0.25, 0.75])
    upper = q3 + (1.5 * (q3 - q1))
    clipped = int((df["weekly_load_ratio"] > upper).sum())
    df["weekly_load_ratio"] = df["weekly_load_ratio"].clip(upper=upper)
    notes["weekly_load_ratio_clipped"] = clipped
    notes["weekly_load_ratio_upper_bound"] = round(float(upper), 4)

    return df, notes


def pick_threshold(y_true, probabilities) -> tuple[float, dict]:
    """在 recall ≥ TARGET_RECALL 的前提下挑 precision 最高的閾值。"""
    best = None

    for threshold in np.arange(0.05, 0.96, 0.01):
        predicted = (probabilities >= threshold).astype(int)
        precision, recall, f1, _ = precision_recall_fscore_support(
            y_true, predicted, average="binary", zero_division=0)

        if recall >= TARGET_RECALL and (best is None or precision > best[1]["precision"]):
            best = (float(threshold), {
                "precision": float(precision),
                "recall": float(recall),
                "f1": float(f1),
            })

    if best is None:
        # 沒有任何閾值達得到目標 recall：回報這件事，不要假裝挑到了
        raise SystemExit(
            f"沒有任何閾值能達到 recall ≥ {TARGET_RECALL}。"
            "這代表模型對這批資料的鑑別力不足，該回頭看特徵而不是調閾值。")

    return best


def main() -> None:
    if not DATA.exists():
        raise SystemExit(
            f"找不到 {DATA}。先執行：dotnet run --project tools/Erp.MlDataGen")

    df = pd.read_csv(DATA)
    raw_rows = len(df)

    df, cleaning_notes = clean(df)

    x = df[FEATURES].to_numpy(dtype=np.float32)
    y = df["was_delayed"].to_numpy(dtype=np.int64)

    # stratify：延遲與未延遲的比例在訓練集與測試集裡要一致，
    # 否則測試集的基準率會漂，評估數字不能跟別次比較
    x_train, x_test, y_train, y_test = train_test_split(
        x, y, test_size=0.25, random_state=RANDOM_STATE, stratify=y)

    # Logistic regression 而不是 gradient boosting：
    # 1200 筆、7 個特徵的規模，boosting 的額外表現換來的是「說不清楚為什麼」，
    # 而這個模組的重點是「每個決策講得清楚」。線性模型的係數本身就是說明。
    # StandardScaler 放進 pipeline 一起匯出，線上推論不必再標準化一次
    # —— 那是另一個 training/serving skew 的經典來源。
    model = Pipeline([
        ("scaler", StandardScaler()),
        ("classifier", LogisticRegression(max_iter=1000, random_state=RANDOM_STATE)),
    ])
    model.fit(x_train, y_train)

    probabilities = model.predict_proba(x_test)[:, 1]
    auc = float(roc_auc_score(y_test, probabilities))
    average_precision = float(average_precision_score(y_test, probabilities))

    threshold, at_threshold = pick_threshold(y_test, probabilities)
    predicted = (probabilities >= threshold).astype(int)
    tn, fp, fn, tp = confusion_matrix(y_test, predicted).ravel()

    default_precision, default_recall, _, _ = precision_recall_fscore_support(
        y_test, (probabilities >= 0.5).astype(int), average="binary", zero_division=0)

    OUT_DIR.mkdir(parents=True, exist_ok=True)

    onnx_model = to_onnx(
        model,
        initial_types=[("features", FloatTensorType([None, len(FEATURES)]))],
        # zipmap 會把機率包成 dict 序列，C# 端要多拆一層而且型別很囉唆。
        # 關掉之後輸出就是一個 [N, 2] 的張量，第二欄是「會延遲」的機率。
        options={id(model.steps[-1][1]): {"zipmap": False}},
        target_opset=17,
    )
    (OUT_DIR / "work-order-delay-model.onnx").write_bytes(onnx_model.SerializeToString())

    coefficients = model.named_steps["classifier"].coef_[0]

    # 黃金樣本：拿幾筆測試集的輸入與 sklearn 算出來的機率一起存進 metadata，
    # 讓 C# 端能驗「ONNX 載進去之後算出來的是同一個數字」。
    #
    # 這條防線抓的是一個不會報錯的失敗：ONNX 吃的是沒有欄位名稱的張量，
    # 特徵順序錯位、或機率取到第 0 欄（不延遲）而不是第 1 欄，
    # 推論都會照跑，只是每個數字都是錯的。沒有這組樣本就沒有人會發現。
    golden_indices = [0, 1, 2, 5, 11, 23, 47]
    golden = [
        {
            "features": [round(float(v), 6) for v in x_test[i]],
            # 存 9 位而不是 6：6 位時有樣本剛好落在捨入的中點上，
            # C# 端用「比到小數第幾位」的方式比較會因為銀行家捨入而差一個 ulp
            "expected_probability": round(float(probabilities[i]), 9),
        }
        for i in golden_indices
    ]

    metadata = {
        "trained_on": date.today().isoformat(),
        "data_source": "模擬資料（HistoricalWorkOrderGenerator，seed=20260913），非真實產線資料",
        "rows_total": raw_rows,
        "rows_train": int(len(x_train)),
        "rows_test": int(len(x_test)),
        "positive_rate": round(float(y.mean()), 4),
        "features": FEATURES,
        "cleaning": cleaning_notes,
        "metrics": {
            "roc_auc": round(auc, 4),
            "average_precision": round(average_precision, 4),
            "threshold": round(threshold, 2),
            "precision_at_threshold": round(at_threshold["precision"], 4),
            "recall_at_threshold": round(at_threshold["recall"], 4),
            "f1_at_threshold": round(at_threshold["f1"], 4),
            "precision_at_0.5": round(float(default_precision), 4),
            "recall_at_0.5": round(float(default_recall), 4),
            "confusion_matrix_at_threshold": {
                "true_negative": int(tn), "false_positive": int(fp),
                "false_negative": int(fn), "true_positive": int(tp),
            },
        },
        "coefficients": {
            name: round(float(value), 4) for name, value in zip(FEATURES, coefficients)
        },
        "intercept": round(float(model.named_steps["classifier"].intercept_[0]), 4),
        "golden_samples": golden,
    }

    (OUT_DIR / "work-order-delay-model.json").write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    print(f"ROC AUC = {auc:.4f}，Average Precision = {average_precision:.4f}")
    print(f"選定閾值 {threshold:.2f}："
          f"precision {at_threshold['precision']:.4f}、recall {at_threshold['recall']:.4f}")
    print(f"  對照預設 0.5：precision {default_precision:.4f}、recall {default_recall:.4f}")
    print(f"  混淆矩陣 TN={tn} FP={fp} FN={fn} TP={tp}")
    print(f"已寫出模型與 metadata 到 {OUT_DIR}")


if __name__ == "__main__":
    main()
