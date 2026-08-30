FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/Job.Core/Job.Core.csproj src/Job.Core/
COPY src/Job.Infrastructure/Job.Infrastructure.csproj src/Job.Infrastructure/
COPY src/Job.Api/Job.Api.csproj src/Job.Api/
COPY nuget.config ./
COPY local-feed/ local-feed/
RUN dotnet restore src/Job.Api/Job.Api.csproj
COPY . .
RUN dotnet publish src/Job.Api/Job.Api.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 5002
ENV ASPNETCORE_URLS=http://+:5002
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
USER app
HEALTHCHECK --interval=30s --timeout=5s --start-period=5s --retries=3 \
  CMD curl -f http://localhost:5002/health || exit 1
ENTRYPOINT ["dotnet", "Job.Api.dll"]
