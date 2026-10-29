# syntax=docker/dockerfile:1
# Pay Query API image. Build from the repository root:
#   docker build -f src/CdcPay.PayQueryApi/Dockerfile -t cdc-pay/pay-query-api .

FROM mcr.microsoft.com/dotnet/sdk:10.0.402-noble AS build
ARG SOURCE_REVISION_ID=
ARG SOURCE_BRANCH=
ARG REPOSITORY_URL=
WORKDIR /repo
COPY src/ src/
COPY contracts/ contracts/
# CdcPay.PayHistory embeds the read-model schema.
COPY db/readmodel/ db/readmodel/
# No .git in the build context: the build identity comes from the arguments above.
RUN dotnet publish src/CdcPay.PayQueryApi/CdcPay.PayQueryApi.csproj -c Release -o /app \
    -p:SourceRevisionId="${SOURCE_REVISION_ID}" \
    -p:SourceBranch="${SOURCE_BRANCH}" \
    -p:RepositoryUrl="${REPOSITORY_URL}" \
    -p:EnableSourceControlManagerQueries=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.14-noble
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
HEALTHCHECK --interval=10s --timeout=5s --start-period=20s --retries=6 \
    CMD ["bash", "-c", "exec 3<>/dev/tcp/127.0.0.1/8080 && printf 'GET /health/live HTTP/1.0\\r\\nHost: localhost\\r\\n\\r\\n' >&3 && head -1 <&3 | grep -q ' 200 '"]
ENTRYPOINT ["dotnet", "CdcPay.PayQueryApi.dll"]
