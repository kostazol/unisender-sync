FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY NuGet.Config Directory.Build.props global.json ./
COPY src/ src/
RUN dotnet publish src/MegaplanUnisender.Api -c Release -a $TARGETARCH --self-contained false -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
USER $APP_UID
EXPOSE 8080
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "MegaplanUnisender.Api.dll"]
