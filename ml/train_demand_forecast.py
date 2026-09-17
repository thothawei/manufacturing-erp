"""物料需求時間序列預測（S6）：三方對照 —— seasonal naive／GBDT／小型 DL。

執行前先產生訓練資料（原始週別需求量，特徵定義的唯一真相在 C# 那邊）：
    dotnet run --project tools/Erp.MlDataGen -- demand-forecast

然後：
    python3 -m venv ml/.venv && ml/.venv/bin/pip install -r ml/requirements.txt
    ml/.venv/bin/python ml/train_demand_forecast.py

輸出：
    src/Erp.Infrastructure/Ml/material-demand-forecast-model.onnx（部署的模型，見下方「為什麼固定部署 GBDT」）
    src/Erp.Infrastructure/Ml/material-demand-forecast-model.json（三方對照結果與訓練當下的評估數據）

**資料是模擬的。** 生成規則是我自己寫的（見 MaterialDemandHistoryGenerator），
所以任何一個方法學得回那條規則都不奇怪 —— 這個模組要證明的是「三方對照做得誠實、
pipeline 接起來了」，不是「這個預測對真實產線有效」。

**特徵工程在這支腳本裡做，不是在 C# 那邊**：跟工單延遲風險模型不同，這個系統目前
沒有任何地方持久化「歷史週別實際需求」這個實體 —— 沒有這張表，C# 端就沒有地方可以
在請求當下重新算一次 lag 特徵，也就沒有「兩邊各自算一次、算出來不一樣」的
training/serving skew 風險可言（風險的前提是「有兩個地方在算同一件事」）。
所以這裡的特徵工程（缺值補值、lag／rolling 特徵）比照 ml/train.py 的 clean() 一樣
留在 Python 這一側；C# 端收到的是呼叫端直接提供的特徵值，不是自己重新推算的。
這個取捨與它的理由寫進了 docs/material-demand-forecast-plan-v1.md。

**為什麼 DL 的角色是 MLPRegressor，不是 N-BEATS 或 1D-CNN**：原規劃寫的是後兩者，
但這個 repo 目前的訓練工具鏈只有 scikit-learn（已經是既有相依），沒有 PyTorch／TensorFlow。
MLPRegressor 一樣是用反向傳播訓練的神經網路，跟 N-BEATS/CNN 的差別在於沒有
時間序列結構化的 inductive bias（不會顯式建模趨勢/季節性分解，也不利用卷積核
抓局部時間模式）。在只有 3 個品項、156 週的資料量下，這個差異對結果的影響
遠小於「資料量本身夠不夠」，而且這一節原本的論點正是「DL 不一定贏」——
換一個更重的框架不會改變這個結論，只會多裝一個新相依。誠實記在這裡，不算後補理由。
"""

import json
from datetime import date
from pathlib import Path

import mlflow
import numpy as np
import pandas as pd
from lightgbm import LGBMRegressor
from onnxmltools import convert_lightgbm
from onnxmltools.convert.common.data_types import FloatTensorType as LgbFloatTensorType
from skl2onnx import to_onnx
from skl2onnx.common.data_types import FloatTensorType
from sklearn.metrics import mean_absolute_error, mean_absolute_percentage_error, mean_squared_error
from sklearn.neural_network import MLPRegressor
from sklearn.pipeline import Pipeline
from sklearn.preprocessing import StandardScaler

ROOT = Path(__file__).resolve().parent.parent
DATA = ROOT / "ml" / "data" / "material-demand-history.csv"
OUT_DIR = ROOT / "src" / "Erp.Infrastructure" / "Ml"

# 同一個本機 file store，跟 ml/train.py 共用，不同 experiment 名稱分開。
MLFLOW_DIR = ROOT / "ml" / "mlruns"
MLFLOW_EXPERIMENT = "material-demand-forecast"

ITEMS = ["PANEL-01", "SCREW-05", "CABLE-07"]

# 順序必須與 C# 端的 MaterialDemandFeatures.FeatureNames 一致 ——
# ONNX 吃的是沒有欄位名稱的張量，順序錯了會照算不誤，只是毫無意義。
FEATURES = [
    "lag_1", "lag_2", "lag_3", "lag_4", "lag_52",
    "rolling_mean_4", "rolling_mean_12", "week_of_year",
    "item_is_panel01", "item_is_screw05", "item_is_cable07",
]

RANDOM_STATE = 20260917

# 時間序列不能隨機切分（會洩漏未來資訊），一律用時間切點。
# 156 週資料，第 52 週起才湊得齊 lag_52，最後 20 週（含一個完整月的份量以上）當測試集。
TRAIN_END_WEEK = 135   # inclusive，最後一筆訓練樣本的 week_index
MIN_TARGET_WEEK = 52   # lag_52 需要至少 52 週的歷史


def interpolate(series: pd.Series) -> pd.Series:
    """缺值用線性內插補，不是刪除或補中位數。

    時間序列補值要保留序列本身的局部趨勢——中位數會把有季節性起伏的週硬拉回
    一個全域中心值，破壞掉正是這個模組要學的訊號。頭尾缺值沒有兩側可以內插，
    退回最近的已知值（bfill/ffill）。
    """
    return series.interpolate(method="linear", limit_direction="both")


def build_features(df_item: pd.DataFrame, item_code: str) -> pd.DataFrame:
    """單一品項的原始週別需求 → 監督式學習的特徵表。"""
    df_item = df_item.sort_values("week_index").reset_index(drop=True)
    demand = interpolate(df_item["demand_qty"])

    rows = []
    for t in range(MIN_TARGET_WEEK, len(demand)):
        rows.append({
            "item_code": item_code,
            "week_index": int(df_item.loc[t, "week_index"]),
            "lag_1": demand[t - 1],
            "lag_2": demand[t - 2],
            "lag_3": demand[t - 3],
            "lag_4": demand[t - 4],
            "lag_52": demand[t - 52],
            "rolling_mean_4": demand[t - 4:t].mean(),
            "rolling_mean_12": demand[t - 12:t].mean(),
            "week_of_year": t % 52,
            "item_is_panel01": 1.0 if item_code == "PANEL-01" else 0.0,
            "item_is_screw05": 1.0 if item_code == "SCREW-05" else 0.0,
            "item_is_cable07": 1.0 if item_code == "CABLE-07" else 0.0,
            "actual_demand": demand[t],
        })

    return pd.DataFrame(rows)


DRIFT_BINS = 10


def drift_profile(x_train) -> dict:
    """每個特徵的漂移偵測基準：分箱邊界 + 訓練集實際比例。跟 ml/train.py 的同名函式
    是同一套設計（去重邊界、對訓練集重新算實際比例，不假設均勻分布），理由也一樣。
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


def metrics_for(y_true, y_pred) -> dict:
    return {
        "mae": round(float(mean_absolute_error(y_true, y_pred)), 3),
        "rmse": round(float(np.sqrt(mean_squared_error(y_true, y_pred))), 3),
        "mape": round(float(mean_absolute_percentage_error(y_true, y_pred)), 4),
    }


def main() -> None:
    if not DATA.exists():
        raise SystemExit(
            f"找不到 {DATA}。先執行：dotnet run --project tools/Erp.MlDataGen -- demand-forecast")

    raw = pd.read_csv(DATA)

    feature_frames = [build_features(raw[raw["item_code"] == item], item) for item in ITEMS]
    df = pd.concat(feature_frames, ignore_index=True)

    train = df[df["week_index"] <= TRAIN_END_WEEK]
    test = df[df["week_index"] > TRAIN_END_WEEK]

    x_train = train[FEATURES].to_numpy(dtype=np.float32)
    y_train = train["actual_demand"].to_numpy(dtype=np.float64)
    x_test = test[FEATURES].to_numpy(dtype=np.float32)
    y_test = test["actual_demand"].to_numpy(dtype=np.float64)

    # --- 方法一：seasonal naive（baseline，不能省）---
    # 預測 = 去年同一週的實際值，就是 lag_52 那一欄本身，不需要訓練
    seasonal_naive_pred = test["lag_52"].to_numpy(dtype=np.float64)

    # --- 方法二：GBDT with lag features ---
    gbdt = LGBMRegressor(
        n_estimators=200, max_depth=4, learning_rate=0.05,
        random_state=RANDOM_STATE, verbosity=-1)
    gbdt.fit(x_train, y_train)
    gbdt_pred = gbdt.predict(x_test)

    # --- 方法三：小型 DL（MLPRegressor，見檔案開頭為什麼不是 N-BEATS/CNN）---
    mlp = Pipeline([
        ("scaler", StandardScaler()),
        ("mlp", MLPRegressor(
            hidden_layer_sizes=(32, 16), max_iter=3000, random_state=RANDOM_STATE,
            early_stopping=True, n_iter_no_change=20)),
    ])
    mlp.fit(x_train, y_train)
    mlp_pred = mlp.predict(x_test)

    # --- 逐品項與整體指標，三個方法並列 ---
    results = {"overall": {
        "seasonal_naive": metrics_for(y_test, seasonal_naive_pred),
        "gbdt": metrics_for(y_test, gbdt_pred),
        "mlp": metrics_for(y_test, mlp_pred),
    }}

    per_item_mape = {"seasonal_naive": [], "gbdt": [], "mlp": []}
    for item in ITEMS:
        mask = (test["item_code"] == item).to_numpy()
        item_result = {
            "seasonal_naive": metrics_for(y_test[mask], seasonal_naive_pred[mask]),
            "gbdt": metrics_for(y_test[mask], gbdt_pred[mask]),
            "mlp": metrics_for(y_test[mask], mlp_pred[mask]),
        }
        results[item] = item_result
        for method in per_item_mape:
            per_item_mape[method].append(item_result[method]["mape"])

    # 決定「哪個方法贏了」用逐品項 MAPE 的平均，不是整體 MAE ——
    # SCREW-05 的需求量級是 PANEL-01 的十幾倍，直接比絕對誤差整體數字會被它主導，
    # MAPE 是無單位的相對誤差，三個品項才比得公平。
    mean_mape = {method: round(float(np.mean(values)), 4) for method, values in per_item_mape.items()}
    winner = min(mean_mape, key=lambda m: mean_mape[m])

    # --- 部署決定：固定用 GBDT ---
    # 跟工單延遲風險模型「刻意只做一個」是同一個理由：這個規模下維護兩個部署產物
    # 換不到對應的價值。GBDT 而不是 MLP，是因為表格資料 + 額外的 lag 特徵，
    # 樹模型通常更穩定也更好解釋（特徵重要性）。真正的三方比較結果（包含
    # seasonal naive 是否其實贏了）誠實記錄在 metadata 裡，不因為部署選擇而被蓋掉。
    deployed_model = "gbdt"

    onnx_model = convert_lightgbm(
        gbdt, initial_types=[("features", LgbFloatTensorType([None, len(FEATURES)]))])
    (OUT_DIR / "material-demand-forecast-model.onnx").write_bytes(onnx_model.SerializeToString())

    # 黃金樣本：測試集裡挑幾筆，連同 sklearn/lightgbm 算出來的預測值一起存進 metadata，
    # C# 端驗 ONNX 載進 .NET 之後算出同一個數字（理由跟 work-order-delay-model 那組一樣：
    # ONNX 是沒有欄位名稱的張量，特徵順序錯位不會報錯，只會算出無意義的數字）。
    golden_indices = [0, 5, 10, 15, min(20, len(test) - 1)]
    golden_indices = sorted(set(i for i in golden_indices if i < len(test)))
    golden = [
        {
            "features": [round(float(v), 6) for v in x_test[i]],
            "expected_prediction": round(float(gbdt_pred[i]), 6),
        }
        for i in golden_indices
    ]

    OUT_DIR.mkdir(parents=True, exist_ok=True)

    metadata = {
        "trained_on": date.today().isoformat(),
        "data_source": "模擬資料（MaterialDemandHistoryGenerator，seed=20260917），非真實產線資料",
        "items": ITEMS,
        "weeks_total": int(raw["week_index"].max()) + 1,
        "rows_train": int(len(train)),
        "rows_test": int(len(test)),
        "train_end_week": TRAIN_END_WEEK,
        "features": FEATURES,
        "drift_profile": drift_profile(x_train),
        "comparison": {
            "method_note": (
                "決勝負用逐品項 MAPE 的平均，不是整體 MAE——三個品項的需求量級差十幾倍，"
                "整體 MAE 會被量級最大的品項主導。"
            ),
            "mean_mape_by_method": mean_mape,
            "winner_by_mape": winner,
            "overall": results["overall"],
            "by_item": {item: results[item] for item in ITEMS},
        },
        "deployed_model": deployed_model,
        "deployment_note": (
            f"固定部署 gbdt，跟贏家（{winner}）"
            + ("一致，不是巧合也不是刻意挑的。" if winner == deployed_model else
               f"不一致——{winner} 在這批測試資料上 MAPE 更低，誠實記在這裡，"
               "不代表 gbdt 比較差；規模與理由見檔案開頭「為什麼固定部署 GBDT」。")
        ),
        "golden_samples": golden,
    }

    (OUT_DIR / "material-demand-forecast-model.json").write_text(
        json.dumps(metadata, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    # 實驗追蹤（S5）：三個方法各開一個 run，同一個 experiment 底下——
    # MLflow UI 的「比較執行」表格本來就是為了這種情境設計的：同一批資料、
    # 同一個切分，三個方法的指標並排，這正是這個模組的核心產出。
    mlflow.set_tracking_uri(f"file:{MLFLOW_DIR}")
    mlflow.set_experiment(MLFLOW_EXPERIMENT)
    run_stamp = date.today().isoformat()

    for method in ("seasonal_naive", "gbdt", "mlp"):
        with mlflow.start_run(run_name=f"{method}-{run_stamp}"):
            mlflow.log_params({
                "method": method,
                "random_state": RANDOM_STATE,
                "train_end_week": TRAIN_END_WEEK,
                "rows_train": int(len(train)),
                "rows_test": int(len(test)),
                "feature_count": len(FEATURES),
            })
            mlflow.log_metrics({
                "mae": results["overall"][method]["mae"],
                "rmse": results["overall"][method]["rmse"],
                "mape": results["overall"][method]["mape"],
                "mean_mape_by_item": mean_mape[method],
            })
            mlflow.set_tags({
                "is_winner": str(method == winner),
                "is_deployed": str(method == deployed_model),
                "data_source": "simulated",
            })

            if method == deployed_model:
                mlflow.log_artifact(str(OUT_DIR / "material-demand-forecast-model.onnx"))
                mlflow.log_artifact(str(OUT_DIR / "material-demand-forecast-model.json"))

    print(f"訓練樣本 {len(train)} 筆、測試樣本 {len(test)} 筆")
    print("整體指標（MAE / RMSE / MAPE）：")
    for method, m in results["overall"].items():
        print(f"  {method:14s} MAE={m['mae']:.2f}  RMSE={m['rmse']:.2f}  MAPE={m['mape']:.2%}")
    print(f"逐品項平均 MAPE：{mean_mape}")
    print(f"贏家（依平均 MAPE）：{winner}")
    print(f"部署：{deployed_model}（{metadata['deployment_note']}）")
    print(f"已寫出模型與 metadata 到 {OUT_DIR}")


if __name__ == "__main__":
    main()
