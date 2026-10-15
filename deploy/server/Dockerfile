FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
WORKDIR /source
COPY Directory.Build.props ./
COPY src/ClearPlate.Core/ClearPlate.Core.csproj src/ClearPlate.Core/
COPY src/ClearPlate.Server/ClearPlate.Server.csproj src/ClearPlate.Server/
RUN dotnet restore src/ClearPlate.Server/ClearPlate.Server.csproj
COPY src/ClearPlate.Core/ src/ClearPlate.Core/
COPY src/ClearPlate.Server/ src/ClearPlate.Server/
COPY src/ClearPlate.Windows/Assets/plate.png src/ClearPlate.Windows/Assets/plate.png
RUN dotnet publish src/ClearPlate.Server/ClearPlate.Server.csproj -c Release --no-restore -p:UseAppHost=false -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish/ ./
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_EnableDiagnostics=0 ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 CMD ["dotnet", "ClearPlate.Server.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "ClearPlate.Server.dll"]
