# 線上展示站的容器。
#
# 兩個階段：SDK 映像負責還原與發佈，執行階段只留 ASP.NET runtime ——
# SDK 映像超過 1 GB，把它整包推上去只是在浪費部署時間與流量。
#
# 這份 Dockerfile 是通用的（Render / Fly.io / Railway / Azure Container Apps 都吃得下），
# 部署設定則各平台一個檔案，見 deploy/。

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# 先只複製專案檔還原相依套件：原始碼改動時這一層仍能命中快取，
# 省掉每次重建都重新 restore 的時間
COPY ManufacturingErp.slnx ./
COPY src/Erp.Domain/Erp.Domain.csproj src/Erp.Domain/
COPY src/Erp.Application/Erp.Application.csproj src/Erp.Application/
COPY src/Erp.Infrastructure/Erp.Infrastructure.csproj src/Erp.Infrastructure/
COPY src/Erp.Api/Erp.Api.csproj src/Erp.Api/
RUN dotnet restore src/Erp.Api/Erp.Api.csproj

COPY src/ src/
RUN dotnet publish src/Erp.Api/Erp.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

# ONNX Runtime（延遲風險模型）依賴 OpenMP 執行期。
# 少了它的症狀是模型載入時擲出原生載入錯誤 —— 而那會被降級成
# 「模型不可用」，服務照常跑但預測功能安靜地消失，所以寧可裝上。
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/*

# 不用 root 跑。展示站是公開的，容器逃逸的風險再低也沒有理由用 root
RUN useradd --create-home --shell /usr/sbin/nologin erp
USER erp

COPY --from=build --chown=erp:erp /app/publish ./

# SQLite 檔放在容器內，每次啟動重建展示資料。
# 免費方案多半沒有持久卷，而這裡正好不需要 —— 展示資料本來就該是乾淨的。
# BaseUrl 與 Model 必須成對設定，這裡兩個都要覆寫。
#
# appsettings.json 預設打本機的 OmniRoute gateway（開發機上跑得起來），
# 模型代號也跟著是 gateway 的格式 anthropic/claude-opus-5。容器裡沒有那個
# gateway，所以 BaseUrl 清空改打官方端點 —— 但只清 BaseUrl 不夠：
# 官方端點不認得帶 provider 前綴的模型代號，有人在展示站設了金鑰就會失敗。
#
# 不清 BaseUrl 的話還有另一個問題：AI 端點的 503 會變成「連不上 gateway」
# 而不是「沒有設定金鑰」，而首頁導覽說的是後者，訊息對不上就成了誤導。
ENV ConnectionStrings__ErpDatabase="Data Source=/tmp/erp.db" \
    ASPNETCORE_ENVIRONMENT=Demo \
    AiAssistant__BaseUrl="" \
    AiAssistant__Model="claude-opus-5" \
    DOTNET_gcServer=0

# PORT 由平台注入（Render、Railway 都是這個慣例）；沒有時退回 8080
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Erp.Api.dll"]
