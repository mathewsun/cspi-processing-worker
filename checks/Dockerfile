FROM mcr.microsoft.com/dotnet/sdk:10.0
WORKDIR /src
COPY . .
RUN dotnet restore checks/CountryAiChecks.csproj && dotnet build checks/CountryAiChecks.csproj -c Release --no-restore
ENTRYPOINT ["dotnet","run","--project","checks/CountryAiChecks.csproj","-c","Release","--no-build"]
