FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY server/Api/Api.csproj server/Api/
RUN dotnet restore server/Api/Api.csproj
COPY server/Api/ server/Api/
RUN dotnet publish server/Api/Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN apt-get update && apt-get install -y --no-install-recommends poppler-utils fonts-dejavu-core && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Api.dll"]
