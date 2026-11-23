# The admin dashboard (the version the admin plugin pins) and the pages the plugins embed, from npm
FROM node:22-alpine AS dashboard
WORKDIR /dash
COPY plugins/admin/package.json plugins/admin/package-lock.json admin/
RUN cd admin && npm ci --ignore-scripts
RUN npm install --no-save --no-package-lock --ignore-scripts --prefix modules \
      @qodalis/cli-dashboard-module-databases @qodalis/cli-dashboard-module-jobs @qodalis/cli-dashboard-module-audit \
      @qodalis/cli-dashboard-module-filesystem @qodalis/cli-dashboard-module-data-explorer \
      @qodalis/cli-dashboard-module-tokens @qodalis/cli-dashboard-module-users

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/Qodalis.Cli.Abstractions/Qodalis.Cli.Abstractions.csproj src/Qodalis.Cli.Abstractions/
COPY src/Qodalis.Cli/Qodalis.Cli.csproj src/Qodalis.Cli/
COPY plugins/filesystem/Qodalis.Cli.Plugin.FileSystem.csproj plugins/filesystem/
COPY plugins/weather/Qodalis.Cli.Plugin.Weather.csproj plugins/weather/
COPY src/Qodalis.Cli.Server/Qodalis.Cli.Server.csproj src/Qodalis.Cli.Server/
RUN dotnet restore src/Qodalis.Cli.Server/Qodalis.Cli.Server.csproj

# Root files referenced by csproj (Pack items: logo, license, readme)
COPY LICENSE ./
COPY README.md ./
COPY assets/ assets/

COPY build/ build/
COPY src/ src/
COPY plugins/ plugins/
COPY --from=dashboard /dash/admin/node_modules plugins/admin/node_modules
COPY --from=dashboard /dash/modules/node_modules node_modules
RUN dotnet publish src/Qodalis.Cli.Server/Qodalis.Cli.Server.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_URLS=http://+:8046
EXPOSE 8046

ENTRYPOINT ["dotnet", "Qodalis.Cli.Server.dll"]
