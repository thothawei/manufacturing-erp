#!/usr/bin/env bash
# 重新產生 docs/images/resume-ai-assistant.png ——「問一句話，AI 自己查完回答」的展示圖。
#
# 為什麼不直接手動截圖：圖上的數字會跟著展示資料變，手截的圖沒人知道是哪一版跑出來的。
# 這支每次都當場打 API、當場從稽核 log 取工具名與耗時，圖裡沒有寫死的數字。
#
# provider 端是 scripts/demo-nl-provider.mjs，不是真的模型 ——
# 工具呼叫與資料是真的，「挑工具」與「把數字寫成句子」是規則寫的。用這張圖時要照實說。
#
# 先決條件：.NET 10 SDK、node、Google Chrome、已啟動的 OmniRoute（npm i -g omniroute）
# 用法：./scripts/demo-ai-screenshot.sh
set -uo pipefail

cd "$(dirname "$0")/.."

STUB_PORT="${STUB_PORT:-8000}"
OMNIROUTE_URL="${OMNIROUTE_URL:-http://localhost:20128}"
ERP_URL="${ERP_URL:-http://localhost:5199}"
LOG_PATH="${LOG_PATH:-/tmp/erp-demo-api.log}"
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

# ── 1. OmniRoute：這支不負責啟動它
if [ "$(http_code "$OMNIROUTE_URL/")" = "000" ]; then
  echo "✗ OmniRoute 沒有在 $OMNIROUTE_URL 回應。先另開一個終端機執行 omniroute。" >&2
  exit 1
fi

if command -v omniroute > /dev/null && ! omniroute providers list 2>/dev/null | grep -q vllm; then
  echo "→ OmniRoute 尚未連 vllm，補一個指向本機 stub 的連線"
  printf 'sk-local-stub' | omniroute providers add vllm --credential-stdin --yes > /dev/null 2>&1
fi

# ── 2. stub provider
if [ "$(http_code "http://127.0.0.1:$STUB_PORT/v1/models")" = "000" ]; then
  STUB_PORT="$STUB_PORT" node scripts/demo-nl-provider.mjs > /tmp/erp-demo-stub.log 2>&1 &
  STARTED_STUB=$!
  wait_for "http://127.0.0.1:$STUB_PORT/v1/models" "stub provider" 30 || exit 1
fi

# ── 3. ERP API：一定要由這支啟動，工具耗時是從它的 log 解析出來的
if [ "$(http_code "$ERP_URL/health")" = "000" ]; then
  ASPNETCORE_ENVIRONMENT=Development \
  AiAssistant__ApiKey="sk-local-stub" \
  AiAssistant__Model="vllm/erp-fake" \
    dotnet run --project src/Erp.Api --configuration Release --urls "$ERP_URL" > "$LOG_PATH" 2>&1 &
  STARTED_ERP=$!
  wait_for "$ERP_URL/health" "ERP API" 120 || { tail -20 "$LOG_PATH" >&2; exit 1; }
else
  echo "✗ $ERP_URL 已經有服務在跑，但工具耗時要從這支自己啟動的 ERP log 取。" >&2
  echo "  先停掉它：lsof -ti tcp:${ERP_URL##*:} | xargs kill" >&2
  exit 1
fi

# ── 4. 問問題、產圖
ERP_URL="$ERP_URL" LOG_PATH="$LOG_PATH" python3 scripts/demo-ai-screenshot.py
