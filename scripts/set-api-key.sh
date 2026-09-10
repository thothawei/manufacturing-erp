#!/usr/bin/env bash
# 從剪貼簿讀取 Anthropic API 金鑰，寫入這個專案的 user-secrets。
#
# 為什麼用剪貼簿而不是互動輸入：嵌入式終端機面板的 stdin 不支援互動 read。
# 為什麼做成腳本：這樣執行時只要打一個短指令，不必複製任何文字，
# 剪貼簿可以維持只有金鑰。
#
# 用法：先複製金鑰（Cmd+C），再執行 ./scripts/set-api-key.sh
# 僅適用 macOS（依賴 pbpaste）。
set -euo pipefail

cd "$(dirname "$0")/.."
export DOTNET_ROOT="${DOTNET_ROOT:-/opt/homebrew/opt/dotnet/libexec}"

KEY="$(pbpaste | tr -d '\n\r ')"

if [[ "$KEY" != sk-ant-* ]]; then
  echo "剪貼簿內容不是 Anthropic 金鑰。"
  echo "  金鑰應以 sk-ant- 開頭，目前剪貼簿長度 ${#KEY} 字元。"
  echo "  請先到 console.anthropic.com 複製金鑰，再執行一次。"
  exit 1
fi

if (( ${#KEY} < 50 )); then
  echo "金鑰長度只有 ${#KEY} 字元，看起來被截斷了，未寫入。"
  exit 1
fi

dotnet user-secrets set "AiAssistant:ApiKey" "$KEY" --project src/Erp.Api > /dev/null
echo "已寫入 user-secrets，長度 ${#KEY} 字元。"
