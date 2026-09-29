FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY MeshCoreMqtt.sln ./
COPY src/MeshCoreMqtt.Core/MeshCoreMqtt.Core.csproj src/MeshCoreMqtt.Core/
COPY src/MeshCoreMqtt.Proxy/MeshCoreMqtt.Proxy.csproj src/MeshCoreMqtt.Proxy/
RUN dotnet restore src/MeshCoreMqtt.Proxy/MeshCoreMqtt.Proxy.csproj
COPY src/MeshCoreMqtt.Core src/MeshCoreMqtt.Core
COPY src/MeshCoreMqtt.Proxy src/MeshCoreMqtt.Proxy
RUN dotnet publish src/MeshCoreMqtt.Proxy/MeshCoreMqtt.Proxy.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:9090
EXPOSE 8883 9090
ENTRYPOINT ["dotnet", "MeshCoreMqtt.Proxy.dll"]
