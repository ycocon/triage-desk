FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /source
COPY global.json ./
COPY apps/api/TriageDesk.Api/TriageDesk.Api.csproj apps/api/TriageDesk.Api/packages.lock.json ./apps/api/TriageDesk.Api/
RUN dotnet restore apps/api/TriageDesk.Api --locked-mode
COPY apps/api/TriageDesk.Api ./apps/api/TriageDesk.Api/
RUN dotnet publish apps/api/TriageDesk.Api -c Release --no-restore -o /publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /publish .
ARG RELEASE_SHA
LABEL org.opencontainers.image.revision=$RELEASE_SHA
ENV Operations__Release=$RELEASE_SHA
ENV ASPNETCORE_HTTP_PORTS=10000
EXPOSE 10000
USER $APP_UID
ENTRYPOINT ["dotnet", "TriageDesk.Api.dll"]
