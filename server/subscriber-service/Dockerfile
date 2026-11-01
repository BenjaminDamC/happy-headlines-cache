# SubscriberService — build from the repo root (context: .).
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/SubscriberService/SubscriberService.csproj src/SubscriberService/
RUN dotnet restore src/SubscriberService/SubscriberService.csproj
COPY src/SubscriberService/ src/SubscriberService/
RUN dotnet publish src/SubscriberService/SubscriberService.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .
EXPOSE 4004
CMD ["dotnet", "SubscriberService.dll"]
