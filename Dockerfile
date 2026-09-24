FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Workbench.csproj ./
RUN dotnet restore Workbench.csproj
COPY . .
RUN dotnet publish Workbench.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /var/lib/workbench/keys && chown -R $APP_UID /var/lib/workbench
ENV DataProtection__KeysPath=/var/lib/workbench/keys
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "Workbench.dll"]
