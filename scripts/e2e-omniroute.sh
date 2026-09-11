#!/usr/bin/env bash
# 端到端驗證：ERP 端點 → tool-use 迴圈 → OmniRoute → provider → 工具查真實資料 → 最終答案。
#
# 為什麼要有這支：單元測試用假的 ILlmClient，wire format 測試用假的 Anthropic 伺服器，
# 兩者都停在 AnthropicLlmClient 的邊界上。gateway 那一層的轉譯（Anthropic 格式
# ←→ OpenAI 格式、工具定義、tool_use/tool_result 來回）沒有任何測試看得到，
# 而那正是最容易默默壞掉的地方。這支把整條打通一次，九個工具逐一驗。
#
# provider 端是 scripts/fake-openai-provider.mjs，不是真的模型 ——
# 要驗的是路徑與轉譯，不是模型答得好不好。
#
# 先決條件：
#   - .NET 10 SDK、node、已安裝並啟動的 OmniRoute（npm i -g omniroute）
#   - OmniRoute 裡要有一個 vllm 連線指向本機 stub（沒有的話這支會自己加）
#
# 用法：./scripts/e2e-omniroute.sh
# 全過回傳 0，任何一項失敗回傳 1。
set -uo pipefail

cd "$(dirname "$0")/.."

STUB_PORT="${STUB_PORT:-8000}"
OMNIROUTE_URL="${OMNIROUTE_URL:-http://localhost:20128}"
ERP_URL="${ERP_URL:-http://localhost:5199}"
STARTED_STUB=""
STARTED_ERP=""

cleanup() {
  [ -n "$STARTED_STUB" ] && kill "$STARTED_STUB" 2>/dev/null
  [ -n "$STARTED_ERP" ] && kill "$STARTED_ERP" 2>/dev/null
  return 0
}
trap cleanup EXIT

http_code() { curl -s -o /dev/null -m 5 --noproxy '*' -w "%{http_code}" "$1" 2>/dev/null; }

wait_for() {  # wait_for <url> <描述> <最多幾秒>
  local deadline=$(( SECONDS + $3 ))
  while [ "$SECONDS" -lt "$deadline" ]; do
    [ "$(http_code "$1")" != "000" ] && return 0
    sleep 2
  done
  echo "✗ 等不到 $2（$1）" >&2
  return 1
}

# ── 1. OmniRoute：這支不負責啟動它，沒開就直接說清楚
if [ "$(http_code "$OMNIROUTE_URL/")" = "000" ]; then
  echo "✗ OmniRoute 沒有在 $OMNIROUTE_URL 回應。先另開一個終端機執行 omniroute。" >&2
  exit 1
fi

# ── 2. stub provider
if [ "$(http_code "http://127.0.0.1:$STUB_PORT/v1/models")" = "000" ]; then
  STUB_PORT="$STUB_PORT" node scripts/fake-openai-provider.mjs > /tmp/erp-e2e-stub.log 2>&1 &
  STARTED_STUB=$!
  wait_for "http://127.0.0.1:$STUB_PORT/v1/models" "stub provider" 30 || exit 1
fi

# OmniRoute 的 vllm 預設就指向 localhost:8000/v1，沒連過就補一個
if command -v omniroute > /dev/null && ! omniroute providers list 2>/dev/null | grep -q vllm; then
  echo "→ OmniRoute 尚未連 vllm，補一個指向本機 stub 的連線"
  printf 'sk-local-stub' | omniroute providers add vllm --credential-stdin --yes > /dev/null 2>&1
fi

# ── 3. ERP API
if [ "$(http_code "$ERP_URL/health")" = "000" ]; then
  ASPNETCORE_ENVIRONMENT=Development \
  AiAssistant__ApiKey="sk-local-stub" \
  AiAssistant__Model="vllm/erp-fake" \
    dotnet run --project src/Erp.Api --configuration Release --urls "$ERP_URL" > /tmp/erp-e2e-api.log 2>&1 &
  STARTED_ERP=$!
  wait_for "$ERP_URL/health" "ERP API" 120 || { tail -20 /tmp/erp-e2e-api.log >&2; exit 1; }
fi

# ── 4. 逐項驗證
PASS=0
FAIL=0

check() {  # check <說明> <問句> <答案裡必須出現的字串>
  local answer
  answer=$(python3 - "$2" <<'PY' | curl -sS -m 90 --noproxy '*' -X POST "$ERP_URL/api/ai-assistant/ask" \
             -H 'Content-Type: application/json' --data @- | python3 -c \
             'import sys,json; d=json.load(sys.stdin); print(d.get("answer") or d.get("detail") or "")'
import json, sys
print(json.dumps({"question": sys.argv[1]}))
PY
)
  if [[ "$answer" == *"$3"* ]]; then
    printf '✓ %s\n' "$1"; PASS=$(( PASS + 1 ))
  else
    printf '✗ %s\n    期待含有：%s\n    實際：%s\n' "$1" "$3" "${answer:0:200}"; FAIL=$(( FAIL + 1 ))
  fi
}

WO="WO-$(date -u +%Y%m%d)-01"

check "search_items 查到料號"            '@@CALL search_items {"keyword":"面板"}@@'                                   '"item_code":"PANEL-01"'
check "get_item_inventory_status 可用量"  '@@CALL get_item_inventory_status {"item_code":"PANEL-01"}@@'                '"available_qty":80'
check "check_material_sufficiency 可製造量" '@@CALL check_material_sufficiency_for_item {"item_code":"TV-100"}@@'      '"max_buildable_qty":40'
check "get_work_order_progress 途程"      "@@CALL get_work_order_progress {\"work_order_no\":\"$WO\"}@@"               '"operation_name":"面板貼合"'
check "list_work_orders_at_risk 風險原因"  '@@CALL list_work_orders_at_risk {}@@'                                      '"risk_reason"'
check "run_mrp_shortage_analysis 建議採購" '@@CALL run_mrp_shortage_analysis {}@@'                                     '"suggested_order_qty"'
check "list_open_purchase_orders 未結採購" '@@CALL list_open_purchase_orders {}@@'                                     '"po_no"'
check "get_quality_inspection_summary 不良" '@@CALL get_quality_inspection_summary {}@@'                               '"failed_qty"'
check "平行呼叫兩個工具都拿到結果"          '@@CALL get_item_inventory_status {"item_code":"PANEL-01"}@@ @@CALL get_item_inventory_status {"item_code":"CABLE-07"}@@' '|||'
check "工具錯誤如實回報"                   '@@CALL get_item_inventory_status {"item_code":"NOPE-999"}@@'               'ENTITY_NOT_FOUND'

# search_documents 需要本機 Ollama。沒有時回 SERVICE_UNAVAILABLE 也算通過 ——
# 這條要守的是「錯誤契約有送到模型手上」，不是「一定查得到文件」
DOC_ANSWER=$(python3 -c 'import json;print(json.dumps({"question":"@@CALL search_documents {\"query\":\"面板色偏怎麼判定\"}@@"}))' \
  | curl -sS -m 90 --noproxy '*' -X POST "$ERP_URL/api/ai-assistant/ask" -H 'Content-Type: application/json' --data @- \
  | python3 -c 'import sys,json;print(json.load(sys.stdin).get("answer",""))')
if [[ "$DOC_ANSWER" == *'"chunks"'* || "$DOC_ANSWER" == *"SERVICE_UNAVAILABLE"* ]]; then
  printf '✓ search_documents（%s）\n' "$([[ "$DOC_ANSWER" == *"SERVICE_UNAVAILABLE"* ]] && echo '沒有 Ollama，回報服務不可用' || echo '查到段落')"
  PASS=$(( PASS + 1 ))
else
  printf '✗ search_documents\n    實際：%s\n' "${DOC_ANSWER:0:200}"; FAIL=$(( FAIL + 1 ))
fi

echo
echo "通過 $PASS 項，失敗 $FAIL 項"
[ "$FAIL" -eq 0 ]
