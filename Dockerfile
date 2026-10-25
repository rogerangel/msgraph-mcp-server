FROM mcr.microsoft.com/dotnet/sdk:10.0.403-noble@sha256:afd0ccff9d306812a69ef0f47458376b6acb544ae866cfc4b8d28ff0eef35ea7 AS build
WORKDIR /src
COPY global.json GraphMcp.slnx ./
COPY apps/graph-mcp/GraphMcp.csproj apps/graph-mcp/packages.lock.json apps/graph-mcp/
RUN dotnet restore apps/graph-mcp/GraphMcp.csproj --locked-mode
COPY apps/graph-mcp/ apps/graph-mcp/
RUN dotnet publish apps/graph-mcp/GraphMcp.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM build AS test
COPY tests/GraphMcp.Tests/GraphMcp.Tests.csproj tests/GraphMcp.Tests/packages.lock.json tests/GraphMcp.Tests/
RUN dotnet restore GraphMcp.slnx --locked-mode
COPY tests/ tests/
RUN dotnet test --solution GraphMcp.slnx --no-restore -c Release

FROM mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd AS runtime
WORKDIR /app
COPY --from=build /out/ ./
USER 1654:1654
ENV DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_ENVIRONMENT=Production \
    Transport__McpPort=8080 \
    Transport__OperatorPort=8081
EXPOSE 8080 8081
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 CMD ["dotnet", "GraphMcp.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "GraphMcp.dll"]
