FROM node:26-bookworm-slim AS frontend
WORKDIR /source
COPY package.json package-lock.json ./
RUN npm ci
COPY angular.json tsconfig.json ./
COPY src/ src/
COPY Data/ Data/
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS gateway
WORKDIR /source
COPY server/Web/Web.csproj server/Web/
COPY server/ServiceDefaults/ServiceDefaults.csproj server/ServiceDefaults/
RUN dotnet restore server/Web/Web.csproj
COPY server/Web/ server/Web/
COPY server/ServiceDefaults/ server/ServiceDefaults/
RUN dotnet publish server/Web/Web.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=gateway /app .
COPY --from=frontend /source/dist/examinator/browser/ ./wwwroot/
USER $APP_UID
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "Web.dll"]
