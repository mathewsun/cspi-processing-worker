FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Cspi.ProcessingWorker.csproj ./
RUN dotnet restore Cspi.ProcessingWorker.csproj
COPY . ./
RUN dotnet publish Cspi.ProcessingWorker.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
USER $APP_UID
ENTRYPOINT ["dotnet", "Cspi.ProcessingWorker.dll"]
