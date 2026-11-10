# Production-shape image for the RRF5 Fiscal Service (ADR-012, FF-014).
# Build from the repository root:
#   docker build -f deploy/docker/Dockerfile --build-arg APP=Api    -t rrf5-fiscal-api .
#   docker build -f deploy/docker/Dockerfile --build-arg APP=Worker -t rrf5-fiscal-worker .
# No certificate, CSC, password or schema package is copied in: secrets come from
# the vault at run time and official artifacts from object storage.
ARG APP=Api

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG APP
WORKDIR /src
COPY Directory.Build.props Directory.Packages.props ./
COPY src/Rrf5.Fiscal.Domain/Rrf5.Fiscal.Domain.csproj                 src/Rrf5.Fiscal.Domain/
COPY src/Rrf5.Fiscal.Application/Rrf5.Fiscal.Application.csproj       src/Rrf5.Fiscal.Application/
COPY src/Rrf5.Fiscal.Regulatory/Rrf5.Fiscal.Regulatory.csproj         src/Rrf5.Fiscal.Regulatory/
COPY src/Rrf5.Fiscal.Infrastructure/Rrf5.Fiscal.Infrastructure.csproj src/Rrf5.Fiscal.Infrastructure/
COPY src/Rrf5.Fiscal.Api/Rrf5.Fiscal.Api.csproj                       src/Rrf5.Fiscal.Api/
COPY src/Rrf5.Fiscal.Worker/Rrf5.Fiscal.Worker.csproj                 src/Rrf5.Fiscal.Worker/
RUN dotnet restore src/Rrf5.Fiscal.${APP}/Rrf5.Fiscal.${APP}.csproj
COPY src/ src/
# Migrations are embedded in Rrf5.Fiscal.Infrastructure (ADR-014).
COPY db/migrations/ db/migrations/
RUN dotnet publish src/Rrf5.Fiscal.${APP}/Rrf5.Fiscal.${APP}.csproj -c Release -o /app --no-restore \
 && mv /app/Rrf5.Fiscal.${APP}.dll /app/app.dll \
 && mv /app/Rrf5.Fiscal.${APP}.deps.json /app/app.deps.json \
 && mv /app/Rrf5.Fiscal.${APP}.runtimeconfig.json /app/app.runtimeconfig.json

# Chiseled: no shell or package manager, non-root (UID 1654). "extra" adds ICU and
# tzdata, so America/Sao_Paulo and pt-BR formatting work (L3).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app ./
ENV TZ=America/Sao_Paulo \
    ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_gcServer=0
USER 1654
EXPOSE 8080
ENTRYPOINT ["dotnet", "app.dll"]
