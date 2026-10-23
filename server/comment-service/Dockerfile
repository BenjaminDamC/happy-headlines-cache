# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY src/CommentService/CommentService.csproj src/CommentService/
RUN dotnet restore src/CommentService/CommentService.csproj

COPY src/CommentService/ src/CommentService/
RUN dotnet publish src/CommentService/CommentService.csproj -c Release -o /app/publish --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 4002
CMD ["dotnet", "CommentService.dll"]
