# Build stage — builds BOTH the ArticleService web API and the ArticleFill
# console worker so the same image can run either (see docker-compose.yml).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/ArticleService/ArticleService.csproj src/ArticleService/
COPY src/ArticleFill/ArticleFill.csproj src/ArticleFill/
RUN dotnet restore src/ArticleService/ArticleService.csproj
RUN dotnet restore src/ArticleFill/ArticleFill.csproj

COPY src/ArticleService/ src/ArticleService/
COPY src/ArticleFill/ src/ArticleFill/
RUN dotnet publish src/ArticleService/ArticleService.csproj -c Release -o /app/service --no-restore
RUN dotnet publish src/ArticleFill/ArticleFill.csproj -c Release -o /app/fill --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/service /app/service
COPY --from=build /app/fill /app/fill
EXPOSE 4001
CMD ["dotnet", "/app/service/ArticleService.dll"]
