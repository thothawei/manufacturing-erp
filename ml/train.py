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

import mlflow
import numpy as np
import pandas as pd
from skl2onnx import to_onnx
from skl2onnx.common.data_types import FloatTensorType
from sklearn.base import clone
from sklearn.calibration import CalibratedClassifierCV
from sklearn.linear_model import LogisticRegression
from sklearn.metrics import (average_precision_score, brier_score_loss,
                             confusion_matrix, precision_recall_fscore_support,
                             roc_auc_score)
from sklearn.model_selection import train_test_split
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import StandardScaler

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "ml" / "data" / "work-order-history.csv"
OUT_DIR = ROOT / "src" / "Erp.Infrastructure" / "Ml"

# 本機 file store，不架伺服器：`ml/.venv/bin/mlflow ui --backend-store-uri ml/mlruns`
# 在本機看比較表。不進 repo（.gitignore），每次重跑會重新產生。
MLFLOW_DIR = ROOT / "ml" / "mlruns"
MLFLOW_EXPERIMENT = "work-order-delay-risk"

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

# 校準評估的 bootstrap 重抽次數。用區間而不是單一數字來判斷「校準有沒有用」，
# 理由見 evaluate_calibration。
CALIBRATION_BOOTSTRAP = 2000

# 分布邊界取 p1/p99 而不是 min/max：後者被單一一筆極端值決定，
# 線上只要有一張工單稍微超過那筆，就會整批被標成分布外。
DISTRIBUTION_LOW_PERCENTILE = 1
DISTRIBUTION_HIGH_PERCENTILE = 99


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


def reliability_bins(y_true, probabilities, bins: int = 10) -> tuple[float, list]:
    """把預測機率分箱，比對「模型說幾成」與「實際幾成」，並算 ECE。

    ECE（expected calibration error）是各箱「預測平均 − 實際比例」的加權平均絕對值。
    它回答的是「0.7 這個數字真的代表七成嗎」，與 AUC 無關 ——
    AUC 只看排序，把所有機率開根號 AUC 一模一樣，但校準全毀。
    """
    edges = np.linspace(0, 1, bins + 1)
    rows = []
    error = 0.0

    for low, high in zip(edges[:-1], edges[1:]):
        in_bin = (probabilities >= low) & (probabilities < high if high < 1 else probabilities <= high)
        count = int(in_bin.sum())

        if count == 0:
            continue

        predicted_mean = float(probabilities[in_bin].mean())
        actual_rate = float(y_true[in_bin].mean())
        error += count / len(probabilities) * abs(predicted_mean - actual_rate)

        rows.append({
            "range": f"{low:.1f}-{high:.1f}",
            "count": count,
            "predicted_mean": round(predicted_mean, 4),
            "actual_rate": round(actual_rate, 4),
        })

    return error, rows


def evaluate_calibration(base_model, x_train, y_train, x_test, y_test, probabilities) -> dict:
    """校準值不值得做 —— 用數字回答，不用直覺。

    logistic regression 最佳化的就是 log loss，輸出本來就接近校準過的機率，
    所以先驗上「不需要校準」是合理的猜測。但猜測不算數，所以這裡真的跑一次
    Platt（sigmoid）與 isotonic，把三組數字並列。

    判準刻意不是「改善超過百分之幾」那種拍腦袋的門檻，而是
    **bootstrap 重抽測試集算 ΔBrier 的 95% 區間，跨 0 就代表這 300 筆資料分不出差別**。
    分不出差別時採用校準，等於多一層沒有證據支持的轉換，還多一個要維護的東西。
    """
    brier_base = float(brier_score_loss(y_test, probabilities))
    ece_base, bins = reliability_bins(y_test, probabilities)

    rng = np.random.default_rng(RANDOM_STATE)
    candidates = {}

    for method in ("sigmoid", "isotonic"):
        calibrated = CalibratedClassifierCV(clone(base_model), method=method, cv=5)
        calibrated.fit(x_train, y_train)
        calibrated_probabilities = calibrated.predict_proba(x_test)[:, 1]

        brier = float(brier_score_loss(y_test, calibrated_probabilities))
        ece, _ = reliability_bins(y_test, calibrated_probabilities)

        deltas = np.empty(CALIBRATION_BOOTSTRAP)
        for i in range(CALIBRATION_BOOTSTRAP):
            sample = rng.integers(0, len(y_test), len(y_test))
            deltas[i] = (brier_score_loss(y_test[sample], calibrated_probabilities[sample])
                         - brier_score_loss(y_test[sample], probabilities[sample]))

        low, high = (float(v) for v in np.percentile(deltas, [2.5, 97.5]))

        candidates[method] = {
            "brier": round(brier, 5),
            "ece": round(ece, 5),
            "delta_brier_median": round(float(np.median(deltas)), 5),
            "delta_brier_ci95": [round(low, 5), round(high, 5)],
            # 區間整段在 0 以下才算「真的比較好」（Brier 越小越好）
            "significantly_better": bool(high < 0),
        }

    applied = next((m for m, r in candidates.items() if r["significantly_better"]), None)

    return {
        "uncalibrated": {"brier": round(brier_base, 5), "ece": round(ece_base, 5)},
        "reliability_bins": bins,
        "candidates": candidates,
        "applied": applied,
        "decision": (
            f"採用 {applied}：ΔBrier 的 95% 區間整段小於 0。"
            if applied else
            "不採用校準：兩種方法的 ΔBrier 95% 區間都跨 0，這 300 筆測試資料分不出差別。"
            "模型輸出維持未校準的原值，並在文件與 API 說明裡標明「機率適合排序，"
            "不保證『0.68 就是六成八會延遲』」。"
        ),
    }


DRIFT_BINS = 10


def drift_profile(x_train) -> dict:
    """每個特徵的漂移偵測基準：分箱邊界 + 訓練集在每一箱的實際比例，給線上算 PSI 用。

    邊界先用 10 等頻分位數切，但**重複值要去掉再對訓練集重新分箱算真正比例**，
    不能假設每箱一定是 1/DRIFT_BINS —— 低基數的類別型特徵（例如 item_overdue_rate
    全資料集只有 5 種不同值）九個分位數切點會切出重複值，這時候「等頻」的前提
    已經不成立，還硬套均勻分布會讓正常資料被誤判成飄移（這是寫測試時撞到的真實案例，
    不是預想出來的邊界情況）。去重後對訓練集重新算一次比例，離散或連續特徵都算得對。
    """
    quantile_probs = np.linspace(0, 1, DRIFT_BINS + 1)[1:-1]
    profile = {}

    for i, name in enumerate(FEATURES):
        column = x_train[:, i]
        edges = sorted({round(float(v), 6) for v in np.quantile(column, quantile_probs)})

        counts = [0] * (len(edges) + 1)
        for value in column:
            bin_index = 0
            while bin_index < len(edges) and value > edges[bin_index]:
                bin_index += 1
            counts[bin_index] += 1

        profile[name] = {
            "edges": edges,
            "proportions": [round(c / len(column), 6) for c in counts],
        }

    return profile


def distribution_bounds(x_train) -> dict:
    """訓練資料每個特徵的分布邊界，給線上推論做分布外（OOD）檢查用。

    模型對沒見過的輸入仍然會給出一個機率，而且看起來跟正常的一樣有自信 ——
    展示資料的 weekly_load_ratio 是 0.125，訓練資料的範圍是 0.5 以上，
    那個機率的可信度比分布內低，但數字本身完全看不出來。所以把邊界一起交給線上。
    """
    return {
        name: {
            "p1": round(float(np.percentile(x_train[:, i], DISTRIBUTION_LOW_PERCENTILE)), 4),
            "p99": round(float(np.percentile(x_train[:, i], DISTRIBUTION_HIGH_PERCENTILE)), 4),
            "min": round(float(x_train[:, i].min()), 4),
            "max": round(float(x_train[:, i].max()), 4),
        }
        for i, name in enumerate(FEATURES)
    }


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

    # 校準評估：模型輸出的 0.68 是不是真的代表「這類工單有 68% 會延遲」。
    # 這一段不改變模型，只回答「要不要改」——結論可能是「不要」，那也是結論。
    calibration = evaluate_calibration(model, x_train, y_train, x_test, y_test, probabilities)

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
        "calibration": calibration,
        "feature_ranges": distribution_bounds(x_train),
        "drift_profile": drift_profile(x_train),
        "golden_samples": golden,
    }

    (OUT_DIR / "work-order-delay-model.json").write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    # 實驗追蹤（S5）：記參數、指標、資料版本、模型檔，本機 file store，不架伺服器。
    # 放在計算完成之後才開始記錄——mlflow 只負責觀測這次訓練，不影響訓練本身的任何行為。
    mlflow.set_tracking_uri(f"file:{MLFLOW_DIR}")
    mlflow.set_experiment(MLFLOW_EXPERIMENT)
    with mlflow.start_run(run_name=f"logistic-regression-{date.today().isoformat()}"):
        mlflow.log_params({
            "model_type": "logistic_regression",
            "random_state": RANDOM_STATE,
            "target_recall": TARGET_RECALL,
            "feature_count": len(FEATURES),
            "rows_total": raw_rows,
            "data_source_seed": 20260913,
        })
        mlflow.log_metrics({
            "roc_auc": auc,
            "average_precision": average_precision,
            "threshold": threshold,
            "precision_at_threshold": at_threshold["precision"],
            "recall_at_threshold": at_threshold["recall"],
            "f1_at_threshold": at_threshold["f1"],
            "calibration_brier_uncalibrated": calibration["uncalibrated"]["brier"],
            "calibration_ece_uncalibrated": calibration["uncalibrated"]["ece"],
        })
        mlflow.set_tags({
            "calibration_applied": calibration["applied"] or "none",
            "data_source": "simulated",
        })
        mlflow.log_artifact(str(OUT_DIR / "work-order-delay-model.onnx"))
        mlflow.log_artifact(str(OUT_DIR / "work-order-delay-model.json"))

    print(f"ROC AUC = {auc:.4f}，Average Precision = {average_precision:.4f}")
    print(f"選定閾值 {threshold:.2f}："
          f"precision {at_threshold['precision']:.4f}、recall {at_threshold['recall']:.4f}")
    print(f"  對照預設 0.5：precision {default_precision:.4f}、recall {default_recall:.4f}")
    print(f"  混淆矩陣 TN={tn} FP={fp} FN={fn} TP={tp}")
    print(f"校準：未校準 Brier {calibration['uncalibrated']['brier']:.5f}、"
          f"ECE {calibration['uncalibrated']['ece']:.5f}")
    for method, result in calibration["candidates"].items():
        print(f"  {method}: Brier {result['brier']:.5f}、ECE {result['ece']:.5f}、"
              f"ΔBrier 95% 區間 {result['delta_brier_ci95']}")
    print(f"  → {calibration['decision']}")
    print(f"已寫出模型與 metadata 到 {OUT_DIR}")


if __name__ == "__main__":
    main()
