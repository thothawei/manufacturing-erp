# CLAUDE.md

給 Claude Code 的專案須知。給人看的在 `README.md`。

## 開發指令

需要 .NET 10 SDK。CI（`.github/workflows/ci.yml`）就是下面四步，本機全過等於 CI 會過：

```bash
dotnet restore
dotnet format --verify-no-changes --no-restore
dotnet build --no-restore --configuration Release -warnaserror
dotnet test --no-build --configuration Release
```

**整條跑完 25–30 秒**（實測：restore 9 秒、format 16 秒、build 12 秒、test 10 秒，258 個測試）。
這四步是快的，直接在前景跑完看結果，不要丟到背景再自己輪詢。

## 不要再犯的錯

### 用 `pgrep -f` 輪詢背景指令

曾經這樣等 `dotnet test`：

```bash
# 壞掉的寫法
for i in $(seq 1 30); do
  if ! pgrep -f "dotnet test" >/dev/null; then break; fi
  sleep 20
done
```

`pgrep -f` 比對的是完整命令列，而**輪詢自己的 shell，命令列裡就含有 `dotnet test` 這串字**，
所以它永遠找得到「一個還在跑的 process」—— 那個 process 就是它自己。`break` 從來不會觸發，
迴圈一定跑滿全部次數。實際工作 47 秒的一輪 CI，被這樣等成將近 30 分鐘。

要等背景工作，就靠 harness 的完成通知，或去看輸出檔有沒有結束標記。
真要用 pgrep，pattern 不能是自己命令列裡出現過的字（例如改成 `[d]otnet test`）。

### 報告沒驗證過的東西

跑不了測試就明講跑不了，不要用「應該可以」帶過。反過來也一樣：
說「裝不起來」之前先把路找完 —— 官方下載站被擋不代表沒有別的來源（見下）。

## 環境

- **雲端 session 裝 .NET**：`builds.dotnet.microsoft.com` 常被網路政策擋（403），
  但 `mcr.microsoft.com` 通得過。用 registry 的 HTTP API 逐層抓 `dotnet/sdk:10.0` 的 blob
  解開就有完整 SDK，不需要 docker daemon。
- **AI 助理預設走本機 OmniRoute gateway**（`localhost:20128`），不是直接打 Anthropic 官方。
  設定與改回官方的方法見 README「AI 助理」。
- 測試數量寫在 README「測試策略」與開頭。動到測試就順手對齊，不要讓它繼續飄。
