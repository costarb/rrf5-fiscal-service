# Discovery-only image for the .NET fiscal POC (portability gate).
# No certificate, password, schema package or fiscal document is copied in:
# they are mounted read-only at run time. See pocs/evidence/container-poc.md.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Rrf5.Fiscal.Poc.csproj ./
RUN dotnet restore
COPY *.cs ./
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
ENTRYPOINT ["dotnet", "Rrf5.Fiscal.Poc.dll"]
