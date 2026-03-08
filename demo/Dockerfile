FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Qodalis.Cli.Abstractions/Qodalis.Cli.Abstractions.csproj src/Qodalis.Cli.Abstractions/
COPY src/Qodalis.Cli/Qodalis.Cli.csproj src/Qodalis.Cli/
COPY plugins/filesystem/Qodalis.Cli.Plugin.FileSystem.csproj plugins/filesystem/
COPY plugins/filesystem-json/Qodalis.Cli.Plugin.FileSystem.Json.csproj plugins/filesystem-json/
COPY plugins/filesystem-sqlite/Qodalis.Cli.Plugin.FileSystem.Sqlite.csproj plugins/filesystem-sqlite/
COPY plugins/filesystem-efcore/Qodalis.Cli.Plugin.FileSystem.EfCore.csproj plugins/filesystem-efcore/
COPY plugins/filesystem-s3/Qodalis.Cli.Plugin.FileSystem.S3.csproj plugins/filesystem-s3/
COPY plugins/weather/Qodalis.Cli.Plugin.Weather.csproj plugins/weather/
COPY demo/Demo.csproj demo/
RUN dotnet restore demo/Demo.csproj

COPY LICENSE ./
COPY README.md ./
COPY assets/ assets/

COPY src/ src/
COPY plugins/ plugins/
COPY demo/ demo/
RUN dotnet publish demo/Demo.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8046
EXPOSE 8046

ENTRYPOINT ["dotnet", "Demo.dll"]
