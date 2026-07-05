FROM node:22-alpine AS client
WORKDIR /src/Sarab.Client
COPY src/Sarab.Client/package*.json ./
RUN npm ci
COPY src/Sarab.Client ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Sarab.slnx ./
COPY src/Sarab.Api ./src/Sarab.Api
COPY tests/Sarab.Tests ./tests/Sarab.Tests
RUN dotnet restore Sarab.slnx
COPY --from=client /src/Sarab.Client/dist ./src/Sarab.Api/wwwroot
RUN dotnet publish src/Sarab.Api/Sarab.Api.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Sarab.Api.dll"]
