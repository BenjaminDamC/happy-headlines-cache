# NewsletterService — build from the repo root (context: .).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/NewsletterService/NewsletterService.csproj src/NewsletterService/
RUN dotnet restore src/NewsletterService/NewsletterService.csproj
COPY src/NewsletterService/ src/NewsletterService/
RUN dotnet publish src/NewsletterService/NewsletterService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 4005
CMD ["dotnet", "NewsletterService.dll"]
