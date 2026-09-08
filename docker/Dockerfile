# Shared image for the API and the Worker.
#
# They run the same code with different entrypoints on purpose: one build, no drift
# between the process that enqueues work and the process that executes it.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG PROJECT=src/ContentPilot.Api/ContentPilot.Api.csproj
WORKDIR /src

# Restore against the project graph first so a source-only change reuses the layer.
COPY global.json Directory.Build.props ./
COPY src/ src/
RUN dotnet restore "$PROJECT"
RUN dotnet publish "$PROJECT" -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG ENTRY=ContentPilot.Api.dll
ENV ENTRY_DLL=$ENTRY \
    DOTNET_EnableDiagnostics=0 \
    ASPNETCORE_HTTP_PORTS=8080

WORKDIR /app
COPY --from=build /app .

# Never run as root. The .NET runtime images ship a non-root `app` user; use it rather
# than creating one, so this keeps working across base-image changes.
USER app

EXPOSE 8080

# ENTRY_DLL lets the same image serve as api, worker, or the migration init step.
ENTRYPOINT ["/bin/sh", "-c", "exec dotnet $ENTRY_DLL \"$@\"", "--"]
