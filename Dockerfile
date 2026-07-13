# Multi-stage build — the SDK image compiles/publishes, the much smaller
# ASP.NET runtime image is what actually ships and runs on Render.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

COPY CafePOS.Api.csproj .
RUN dotnet restore

COPY . .
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# Render assigns the port at runtime via $PORT; ASPNETCORE_URLS is set from
# that in the start command (see render.yaml / dashboard "Start Command").
EXPOSE 10000
ENV ASPNETCORE_URLS=http://+:10000

ENTRYPOINT ["dotnet", "CafePOS.Api.dll"]
