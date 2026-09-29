FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY MeshCoreMqtt.sln ./
COPY src/MeshCoreMqtt.Core/MeshCoreMqtt.Core.csproj src/MeshCoreMqtt.Core/
COPY src/MeshCoreMqtt.Api/MeshCoreMqtt.Api.csproj src/MeshCoreMqtt.Api/
RUN dotnet restore src/MeshCoreMqtt.Api/MeshCoreMqtt.Api.csproj
COPY src/MeshCoreMqtt.Core src/MeshCoreMqtt.Core
COPY src/MeshCoreMqtt.Api src/MeshCoreMqtt.Api
RUN dotnet publish src/MeshCoreMqtt.Api/MeshCoreMqtt.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "MeshCoreMqtt.Api.dll"]
