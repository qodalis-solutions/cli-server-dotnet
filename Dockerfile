FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY src/Qodalis.Cli.Abstractions/Qodalis.Cli.Abstractions.csproj src/Qodalis.Cli.Abstractions/
COPY src/Qodalis.Cli/Qodalis.Cli.csproj src/Qodalis.Cli/
COPY plugins/weather/WeatherModule.csproj plugins/weather/
COPY src/Qodalis.Cli.Server/Qodalis.Cli.Server.csproj src/Qodalis.Cli.Server/
RUN dotnet restore src/Qodalis.Cli.Server/Qodalis.Cli.Server.csproj

# Root files referenced by csproj (Pack items: logo, license, readme)
COPY LICENSE ./
COPY README.md ./
COPY assets/ assets/

COPY src/ src/
COPY plugins/ plugins/
RUN dotnet publish src/Qodalis.Cli.Server/Qodalis.Cli.Server.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8046
EXPOSE 8046

ENTRYPOINT ["dotnet", "Qodalis.Cli.Server.dll"]
