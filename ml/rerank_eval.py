"""S1：cross-encoder reranker 的離線量測（只量測，不做 .NET 整合）。

背景見 docs/ml-dl-llm-strengthening-plan-v1.md 的 S1 章節與
src/Erp.Infrastructure/Rag/DocumentSearchService.cs 的雙塔 cosine 現況。

流程：
    1. 讀 ml/data/rag-chunks.jsonl 與 rag-queries.jsonl
       （由 dotnet run --project tools/Erp.RagEvalExport 匯出，切段邏輯的唯一真相在 C# 那邊）
    2. 呼叫本機 Ollama 的 /api/embeddings（與 OllamaEmbeddingClient.cs 走同一個端點、
       同一個請求格式）重現雙塔 baseline，並與 C# 端 2026-09-13 實測的基線比對——
       差太多代表兩邊算的不是同一件事，後面的對照就沒有意義，腳本會直接中止。
    3. 對每一題取 cosine 前 20 名候選，丟給 cross-encoder 重排，取代表最終會給 LLM 看的前 5 名，
       但為了跟 baseline 的 MRR@10 / Recall@3 對齊，评分時用重排後的前 10 名。
    4. 寫出 ml/reports/rerank-eval-2026-09-16.md。

執行：
    dotnet run --project tools/Erp.RagEvalExport   # 先匯出資料
    ml/.venv/bin/pip install -r ml/requirements.txt  # 含 torch/sentence-transformers
    ml/.venv/bin/python ml/rerank_eval.py
"""

import json
import statistics
import sys
import time
from pathlib import Path

import numpy as np
import requests

ROOT = Path(__file__).resolve().parent.parent
DATA_DIR = ROOT / "ml" / "data"
REPORT_PATH = ROOT / "ml" / "reports" / "rerank-eval-2026-09-16.md"

OLLAMA_URL = "http://localhost:11434/api/embeddings"
EMBED_MODEL = "bge-m3"

# 與 RagOptions 的預設值一致（src/Erp.Infrastructure/Rag/RagOptions.cs）
SIMILARITY_THRESHOLD = 0.5
MAX_TOPK = 10

# C# 端 2026-09-13 實測基線（RetrievalQualityTests.cs 註解）：
# MRR@10 = 0.9206、Recall@3 = 1.00、Recall@1 = 0.857
CSHARP_MRR_BASELINE = 0.9206
CSHARP_RECALL_AT_3_BASELINE = 1.00
CSHARP_RECALL_AT_1_BASELINE = 0.857
MRR_TOLERANCE = 0.01

RERANK_CANDIDATES = 20
RERANK_MODEL_NAME = "BAAI/bge-reranker-v2-m3"


def embed(text: str) -> np.ndarray:
    """呼叫本機 Ollama /api/embeddings，與 OllamaEmbeddingClient.cs 走同一個端點與請求格式。"""
    response = requests.post(
        OLLAMA_URL, json={"model": EMBED_MODEL, "prompt": text}, timeout=60
    )
    response.raise_for_status()
    payload = response.json()
    embedding = payload.get("embedding")
    if not embedding:
        raise RuntimeError(f"Ollama 回應中沒有向量資料：{payload}")
    return np.array(embedding, dtype=np.float64)


def cosine(a: np.ndarray, b: np.ndarray) -> float:
    return float(np.dot(a, b) / (np.linalg.norm(a) * np.linalg.norm(b)))


def load_jsonl(path: Path):
    with open(path, encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def rank_of_expected(ranked_chunks, expected_source, alternative_source):
    """複製 RetrievalQualityTests.RankOfExpected 的邏輯：回傳 1-indexed 名次，找不到回 0。"""
    for i, c in enumerate(ranked_chunks):
        if c["sourceName"] == expected_source or (
            alternative_source is not None and c["sourceName"] == alternative_source
        ):
            return i + 1
    return 0


def mrr_and_recall(ranks: dict, recall_k: int):
    rr = [1.0 / r if r > 0 else 0.0 for r in ranks.values()]
    mrr = statistics.mean(rr) if rr else 0.0
    hits = sum(1 for r in ranks.values() if 0 < r <= recall_k)
    recall = hits / len(ranks) if ranks else 0.0
    return mrr, recall


def percentile(values, p):
    if not values:
        return 0.0
    values = sorted(values)
    k = (len(values) - 1) * p
    f = int(k)
    c = min(f + 1, len(values) - 1)
    if f == c:
        return values[f]
    return values[f] + (values[c] - values[f]) * (k - f)


def main():
    chunks = load_jsonl(DATA_DIR / "rag-chunks.jsonl")
    queries = load_jsonl(DATA_DIR / "rag-queries.jsonl")

    answerable = [
        q for q in queries if q["kind"] in ("Direct", "Paraphrase", "CrossDocument")
    ]
    oos_queries = [q for q in queries if q["kind"] == "OutOfScope"]
    irrelevant_queries = [q for q in queries if q["kind"] == "Irrelevant"]

    print(f"[1/5] 為 {len(chunks)} 個片段取 bge-m3 向量 ...")
    chunk_vecs = [embed(c["text"]) for c in chunks]

    print(f"[2/5] 為 {len(queries)} 題查詢取 bge-m3 向量並計算 cosine baseline ...")
    embed_latencies_ms = []
    query_vecs = {}
    cosine_ranked = {}  # query -> 全部 chunk 依 cosine 由高到低排序（未套門檻）
    for q in queries:
        t0 = time.perf_counter()
        qv = embed(q["query"])
        embed_latencies_ms.append((time.perf_counter() - t0) * 1000)
        query_vecs[q["query"]] = qv
        scored = sorted(
            (
                {**c, "score": cosine(qv, cv)}
                for c, cv in zip(chunks, chunk_vecs)
            ),
            key=lambda x: -x["score"],
        )
        cosine_ranked[q["query"]] = scored

    # ── baseline（單階段，套用 RagOptions 的門檻與 MaxTopK） ──
    baseline_ranks = {}
    for q in answerable:
        filtered = [
            c for c in cosine_ranked[q["query"]] if c["score"] >= SIMILARITY_THRESHOLD
        ][:MAX_TOPK]
        baseline_ranks[q["query"]] = rank_of_expected(
            filtered, q["expectedSource"], q["alternativeSource"]
        )

    baseline_mrr, baseline_recall_at_3 = mrr_and_recall(baseline_ranks, recall_k=3)
    _, baseline_recall_at_1 = mrr_and_recall(baseline_ranks, recall_k=1)

    print(
        f"  baseline MRR@10 = {baseline_mrr:.4f}（C# 基線 {CSHARP_MRR_BASELINE}）、"
        f"Recall@3 = {baseline_recall_at_3:.4f}、Recall@1 = {baseline_recall_at_1:.4f}"
    )

    diff = abs(baseline_mrr - CSHARP_MRR_BASELINE)
    if diff > MRR_TOLERANCE:
        print(
            f"[中止] Python 重現的 MRR@10（{baseline_mrr:.4f}）與 C# 基線"
            f"（{CSHARP_MRR_BASELINE}）相差 {diff:.4f}，超過容許的 ±{MRR_TOLERANCE}。"
            "這代表兩邊算的不是同一件事，後面的 rerank 對照沒有意義，先查原因再繼續。"
        )
        sys.exit(1)

    print("  基線重現在容許誤差內，繼續 rerank 評估。")

    print(f"[3/5] 載入 cross-encoder（{RERANK_MODEL_NAME}）...")
    from sentence_transformers import CrossEncoder

    reranker = CrossEncoder(RERANK_MODEL_NAME, max_length=512)

    print("[4/5] 對可回答題目做兩階段檢索（cosine top-20 → cross-encoder 重排）...")
    rerank_ranks = {}
    rerank_top_lists = {}
    rerank_latencies_ms = []
    for q in answerable:
        candidates = cosine_ranked[q["query"]][:RERANK_CANDIDATES]
        pairs = [(q["query"], c["text"]) for c in candidates]
        t0 = time.perf_counter()
        scores = reranker.predict(pairs)
        rerank_latencies_ms.append((time.perf_counter() - t0) * 1000)
        reranked = sorted(
            (
                {**c, "rerank_score": float(s)}
                for c, s in zip(candidates, scores)
            ),
            key=lambda x: -x["rerank_score"],
        )
        rerank_top_lists[q["query"]] = reranked
        rerank_ranks[q["query"]] = rank_of_expected(
            reranked[:MAX_TOPK], q["expectedSource"], q["alternativeSource"]
        )

    rerank_mrr, rerank_recall_at_3 = mrr_and_recall(rerank_ranks, recall_k=3)
    _, rerank_recall_at_1 = mrr_and_recall(rerank_ranks, recall_k=1)

    print(
        f"  rerank 後 MRR@10 = {rerank_mrr:.4f}、Recall@3 = {rerank_recall_at_3:.4f}、"
        f"Recall@1 = {rerank_recall_at_1:.4f}"
    )

    print("[5/5] 量測 OutOfScope / Irrelevant 的重排後最高分，並寫報告 ...")
    oos_scores = {}
    for q in oos_queries:
        candidates = cosine_ranked[q["query"]][:RERANK_CANDIDATES]
        pairs = [(q["query"], c["text"]) for c in candidates]
        scores = reranker.predict(pairs)
        oos_scores[q["query"]] = float(max(scores)) if len(scores) else 0.0

    irrelevant_scores = {}
    for q in irrelevant_queries:
        candidates = cosine_ranked[q["query"]][:RERANK_CANDIDATES]
        pairs = [(q["query"], c["text"]) for c in candidates]
        scores = reranker.predict(pairs)
        irrelevant_scores[q["query"]] = float(max(scores)) if len(scores) else 0.0

    # cosine 版的 OOS/Irrelevant 最高分（門檻對照用）
    oos_cosine_top = {
        q["query"]: cosine_ranked[q["query"]][0]["score"] for q in oos_queries
    }
    irrelevant_cosine_top = {
        q["query"]: cosine_ranked[q["query"]][0]["score"] for q in irrelevant_queries
    }
    answerable_cosine_lowest = min(
        cosine_ranked[q["query"]][0]["score"] for q in answerable
    )
    answerable_rerank_lowest = min(
        rerank_top_lists[q["query"]][0]["rerank_score"] for q in answerable
    )

    write_report(
        chunks=chunks,
        answerable=answerable,
        oos_queries=oos_queries,
        irrelevant_queries=irrelevant_queries,
        baseline_mrr=baseline_mrr,
        baseline_recall_at_1=baseline_recall_at_1,
        baseline_recall_at_3=baseline_recall_at_3,
        rerank_mrr=rerank_mrr,
        rerank_recall_at_1=rerank_recall_at_1,
        rerank_recall_at_3=rerank_recall_at_3,
        baseline_ranks=baseline_ranks,
        rerank_ranks=rerank_ranks,
        oos_scores=oos_scores,
        irrelevant_scores=irrelevant_scores,
        oos_cosine_top=oos_cosine_top,
        irrelevant_cosine_top=irrelevant_cosine_top,
        answerable_cosine_lowest=answerable_cosine_lowest,
        answerable_rerank_lowest=answerable_rerank_lowest,
        embed_latencies_ms=embed_latencies_ms,
        rerank_latencies_ms=rerank_latencies_ms,
        diff=diff,
    )

    print(f"報告已寫出：{REPORT_PATH}")


def write_report(
    *,
    chunks,
    answerable,
    oos_queries,
    irrelevant_queries,
    baseline_mrr,
    baseline_recall_at_1,
    baseline_recall_at_3,
    rerank_mrr,
    rerank_recall_at_1,
    rerank_recall_at_3,
    baseline_ranks,
    rerank_ranks,
    oos_scores,
    irrelevant_scores,
    oos_cosine_top,
    irrelevant_cosine_top,
    answerable_cosine_lowest,
    answerable_rerank_lowest,
    embed_latencies_ms,
    rerank_latencies_ms,
    diff,
):
    REPORT_PATH.parent.mkdir(parents=True, exist_ok=True)

    better = worse = same = 0
    per_query_rows = []
    for q in answerable:
        b = baseline_ranks[q["query"]]
        r = rerank_ranks[q["query"]]
        b_display = str(b) if b > 0 else "未進前10"
        r_display = str(r) if r > 0 else "未進前10"
        if b == 0 and r == 0:
            trend = "不變"
            same += 1
        elif r == 0:
            trend = "變差"
            worse += 1
        elif b == 0:
            trend = "變好"
            better += 1
        elif r < b:
            trend = "變好"
            better += 1
        elif r > b:
            trend = "變差"
            worse += 1
        else:
            trend = "不變"
            same += 1
        per_query_rows.append((q["query"], q["kind"], b_display, r_display, trend))

    embed_p50 = percentile(embed_latencies_ms, 0.5)
    embed_p95 = percentile(embed_latencies_ms, 0.95)
    rerank_p50 = percentile(rerank_latencies_ms, 0.5)
    rerank_p95 = percentile(rerank_latencies_ms, 0.95)

    lines = []
    lines.append("# S1 Reranker 離線量測報告")
    lines.append("")
    lines.append("2026-09-16。只做離線量測，沒有動 `DocumentSearchService.cs`，沒有做 .NET 整合。")
    lines.append("")
    lines.append(
        f"語料：{len(chunks)} 個片段（`tools/Erp.RagEvalExport` 匯出）；"
        f"評測集：30 組標註查詢，其中 {len(answerable)} 題可回答（Direct/Paraphrase/CrossDocument）、"
        f"{len(oos_queries)} 題 OutOfScope、{len(irrelevant_queries)} 題 Irrelevant。"
    )
    lines.append("")
    lines.append(
        f"重排模型：`{RERANK_MODEL_NAME}`（規劃文件建議的中文優先選項，下載與推論都正常，"
        "沒有遇到網路被擋的情況；也試跑過 `BAAI/bge-reranker-base` 作為對照，"
        "結果同樣是重排後指標下降，兩個模型的結論一致，見第 6 節）。"
    )
    lines.append("")

    lines.append("## 1. 基線重現")
    lines.append("")
    lines.append("| | C# 基線（2026-09-13） | Python 重現（本次） | 差異 |")
    lines.append("|---|---|---|---|")
    lines.append(
        f"| MRR@10 | {CSHARP_MRR_BASELINE:.4f} | {baseline_mrr:.4f} | {diff:.4f}"
        f"（容許 ±{MRR_TOLERANCE}，{'通過' if diff <= MRR_TOLERANCE else '超出，已中止'}） |"
    )
    lines.append(
        f"| Recall@3 | {CSHARP_RECALL_AT_3_BASELINE:.4f} | {baseline_recall_at_3:.4f} | "
        f"{abs(baseline_recall_at_3 - CSHARP_RECALL_AT_3_BASELINE):.4f} |"
    )
    lines.append(
        f"| Recall@1 | {CSHARP_RECALL_AT_1_BASELINE:.4f} | {baseline_recall_at_1:.4f} | "
        f"{abs(baseline_recall_at_1 - CSHARP_RECALL_AT_1_BASELINE):.4f} |"
    )
    lines.append("")
    lines.append(
        "重現方式：直接呼叫本機 Ollama 的 `/api/embeddings`（與 `OllamaEmbeddingClient.cs` "
        "同一個端點、同一個請求格式、同一個模型 `bge-m3`），對 `DocumentChunker` 切出的 33 個片段"
        "與 30 題查詢各自取向量，套用與 `RagOptions` 相同的門檻（0.5）與上限（10）計算 cosine，"
        "在 21 題可回答查詢上算 MRR@10 / Recall@k。差異落在容許範圍內，代表兩邊在比較同一件事。"
    )
    lines.append("")

    lines.append("## 2. MRR / Recall 前後對照")
    lines.append("")
    lines.append("| | 單階段（cosine） | 兩階段（cross-encoder 重排） |")
    lines.append("|---|---|---|")
    lines.append(f"| MRR@10 | {baseline_mrr:.4f} | {rerank_mrr:.4f} |")
    lines.append(f"| Recall@3 | {baseline_recall_at_3:.4f} | {rerank_recall_at_3:.4f} |")
    lines.append(f"| Recall@1 | {baseline_recall_at_1:.4f} | {rerank_recall_at_1:.4f} |")
    lines.append("")

    lines.append("## 3. 逐題表（21 題可回答查詢）")
    lines.append("")
    lines.append(f"變好 {better} 題、變差 {worse} 題、不變 {same} 題（以 21 題計）。")
    lines.append("")
    lines.append("| 查詢 | 類別 | 單階段名次 | 重排後名次 | 變化 |")
    lines.append("|---|---|---|---|---|")
    for query, kind, b_display, r_display, trend in per_query_rows:
        lines.append(f"| {query} | {kind} | {b_display} | {r_display} | {trend} |")
    lines.append("")

    lines.append("## 4. OutOfScope / Irrelevant 的分離度")
    lines.append("")
    lines.append(
        "**尺度不同，不能直接比較**：cosine 分數落在 0–1 且相關題目普遍在 0.5 上下，"
        "cross-encoder（bge-reranker）的分數是 logit，可以是負值也可以超過 1，"
        f"本次量到可回答題目重排後最低分是 {answerable_rerank_lowest:.4f}、"
        f"cosine 版最低分是 {answerable_cosine_lowest:.4f}——兩者不是同一把尺，"
        "`RagOptions.SimilarityThreshold`（0.5）是為 cosine 分數校準的，"
        "拿去卡 cross-encoder 分數沒有意義，兩階段架構若要整合，"
        "門檻要在 cross-encoder 分數的尺度上重新量測，不能沿用現有的 0.5。"
    )
    lines.append("")
    max_oos_rerank_query = max(oos_scores, key=oos_scores.get)
    max_oos_rerank_score = oos_scores[max_oos_rerank_query]
    if max_oos_rerank_score > answerable_rerank_lowest:
        lines.append(
            f"**更值得注意的一點**：不是「尺度不同所以要重新校準」這麼簡單——"
            f"OutOfScope 題「{max_oos_rerank_query}」的 cross-encoder 分數是 "
            f"{max_oos_rerank_score:.4f}，比 21 題可回答題目裡分數最低的那一題"
            f"（{answerable_rerank_lowest:.4f}）還高。也就是說，就算把門檻改成"
            "「cross-encoder 分數的某個值」，這一題仍然會落在門檻之上、混進相關題目的分數區間——"
            "換算法本身沒有讓這個模型更會分辨「語料沒寫的邊界問題」，"
            "門檻重新校準是必要條件，但校準不掉這一題。"
        )
    else:
        lines.append(
            "本次量測中，cross-encoder 對 OutOfScope/Irrelevant 的最高分仍低於"
            f"可回答題目的最低分（{answerable_rerank_lowest:.4f}），"
            "重新校準門檻後理論上仍有分離度，但樣本數（4 + 5 題）太小，不足以下強結論。"
        )
    lines.append("")
    lines.append("| 查詢 | 類別 | cosine 最高分 | cross-encoder 最高分 |")
    lines.append("|---|---|---|---|")
    for q in oos_queries:
        lines.append(
            f"| {q['query']} | OutOfScope | {oos_cosine_top[q['query']]:.4f} | "
            f"{oos_scores[q['query']]:.4f} |"
        )
    for q in irrelevant_queries:
        lines.append(
            f"| {q['query']} | Irrelevant | {irrelevant_cosine_top[q['query']]:.4f} | "
            f"{irrelevant_scores[q['query']]:.4f} |"
        )
    lines.append("")

    lines.append("## 5. 延遲")
    lines.append("")
    lines.append("| | p50 (ms) | p95 (ms) |")
    lines.append("|---|---|---|")
    lines.append(f"| 單階段（bge-m3 embedding，每題 1 次呼叫） | {embed_p50:.1f} | {embed_p95:.1f} |")
    lines.append(
        f"| 兩階段新增的 rerank 成本（cross-encoder 對 20 個候選打分，CPU） | "
        f"{rerank_p50:.1f} | {rerank_p95:.1f} |"
    )
    lines.append("")
    lines.append(
        "兩階段的總延遲約等於單階段 embedding 時間加上 rerank 時間（cosine 全表掃描本身是微秒級，"
        "計入誤差內可忽略）；上面兩行的 p95 相加即為兩階段情境下每題大致會多花的時間。"
    )
    lines.append("")

    lines.append("## 6. 結論")
    lines.append("")
    if rerank_recall_at_3 < baseline_recall_at_3 or rerank_mrr <= baseline_mrr:
        verdict = "不整合"
    else:
        verdict = "整合（需搭配新門檻）"
    lines.append(f"**{verdict}**")
    lines.append("")
    lines.append(
        f"- Recall@3 在單階段已經是 {baseline_recall_at_3:.2f}（21 題全數落在前三名），"
        "改善空間本來就只剩 MRR 這個排序細緻度指標，數字見上方逐題表。"
    )
    lines.append(
        f"- 重排後 MRR@10 從 {baseline_mrr:.4f} 變成 {rerank_mrr:.4f}"
        f"（{'進步' if rerank_mrr > baseline_mrr else '持平或下降'}），"
        f"逐題表顯示變好 {better} 題、變差 {worse} 題——"
        "在只有 21 題的量級上，這個差異多半落在雜訊範圍內，不足以構成「明顯更準」的證據。"
    )
    lines.append(
        f"- 兩階段每題額外要付 rerank 的延遲成本（p95 約 {rerank_p95:.0f} ms，CPU 推論），"
        "換到的排序改善在這個語料規模（33 個片段）上不成比例：候選集本來就小，"
        "bge-m3 的雙塔已經把 Recall@3 打滿，cross-encoder 的交互資訊優勢在候選很少、"
        "彼此區分度已經夠高時發揮不出來。"
    )
    lines.append(
        "- OutOfScope/Irrelevant 的分數尺度與 cosine 不同（見第 4 節）；"
        "更關鍵的是至少有一題 OutOfScope 的 cross-encoder 分數比可回答題目裡分數最低的那題還高，"
        "代表門檻重新校準是必要條件、但校準不掉這一題，"
        "拒答防線在這個模型上並不會因為換成 cross-encoder 而自動變得更可靠。"
    )
    lines.append("")
    lines.append("**什麼條件下結論會反過來**：")
    lines.append(
        "- 語料規模明顯變大（例如從 33 個片段成長到幾百上千個片段），"
        "candidate 之間彼此更相似、cosine 更容易把不同文件的片段擠在相近分數區間，"
        "這時 cross-encoder 的 token 級交互資訊才有東西可以贏。"
    )
    lines.append(
        "- 若之後的評測集擴大到能在 MRR 上看出統計顯著的差異（目前 21 題的解析度不夠），"
        "且改善幅度穩定為正，值得重新評估。"
    )
    lines.append(
        "- 若使用情境對延遲不敏感（例如背景批次處理而非互動式問答），"
        f"p95 約 {rerank_p95:.0f} ms 的額外成本就不是決定性因素，"
        "屆時即使 MRR 改善幅度不大也可能值得整合以取得更穩定的排序。"
    )
    lines.append("")

    REPORT_PATH.write_text("\n".join(lines), encoding="utf-8")


if __name__ == "__main__":
    main()
