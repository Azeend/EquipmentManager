FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY EquipmentManager.csproj ./
RUN dotnet restore EquipmentManager.csproj
COPY Components/ Components/
COPY Models/ Models/
COPY Services/ Services/
COPY Validators/ Validators/
COPY wwwroot/ wwwroot/
COPY Program.cs appsettings.json ./
RUN dotnet publish EquipmentManager.csproj -c Release --no-restore -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_URLS=http://0.0.0.0:10000
EXPOSE 10000
COPY --from=build /app/publish ./
USER $APP_UID
ENTRYPOINT ["dotnet", "EquipmentManager.dll"]
