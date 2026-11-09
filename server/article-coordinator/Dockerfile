# ArticleCoordinator — build from the repo root (context: .).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/ArticleCoordinator/ArticleCoordinator.csproj src/ArticleCoordinator/
RUN dotnet restore src/ArticleCoordinator/ArticleCoordinator.csproj
COPY src/ArticleCoordinator/ src/ArticleCoordinator/
RUN dotnet publish src/ArticleCoordinator/ArticleCoordinator.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 4007
CMD ["dotnet", "ArticleCoordinator.dll"]
