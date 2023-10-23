#See https://aka.ms/customizecontainer to learn how to customize your debug container and how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS base
WORKDIR /app
EXPOSE 5555
ENV ASPNETCORE_URLS=http://*:5555

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["HomeYarp.webapi/HomeYarp.webapi.csproj", "HomeYarp.webapi/"]
RUN dotnet restore "HomeYarp.webapi/HomeYarp.webapi.csproj"
COPY . .
WORKDIR "/src/HomeYarp.webapi"
RUN dotnet build "HomeYarp.webapi.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "HomeYarp.webapi.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "HomeYarp.webapi.dll"]