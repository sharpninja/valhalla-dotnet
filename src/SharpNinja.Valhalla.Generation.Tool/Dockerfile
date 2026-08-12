# syntax=docker/dockerfile:1.7
ARG SOURCE_COMMIT=unknown

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG SOURCE_COMMIT
WORKDIR /source

COPY Directory.Build.props Directory.Packages.props NuGet.config ./
COPY src/SharpNinja.Valhalla/SharpNinja.Valhalla.csproj src/SharpNinja.Valhalla/
COPY src/SharpNinja.Valhalla.Generation/SharpNinja.Valhalla.Generation.csproj src/SharpNinja.Valhalla.Generation/
COPY src/SharpNinja.Valhalla.Generation.Tool/SharpNinja.Valhalla.Generation.Tool.csproj src/SharpNinja.Valhalla.Generation.Tool/
RUN dotnet restore src/SharpNinja.Valhalla.Generation.Tool/SharpNinja.Valhalla.Generation.Tool.csproj

COPY src/ src/
RUN dotnet publish src/SharpNinja.Valhalla.Generation.Tool/SharpNinja.Valhalla.Generation.Tool.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish \
    -p:SourceRevisionId=$SOURCE_COMMIT \
    -p:ContinuousIntegrationBuild=true

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
ARG SOURCE_COMMIT=unknown
LABEL org.opencontainers.image.title="SharpNinja Valhalla Generation"
LABEL org.opencontainers.image.version="2.0.0"
LABEL org.opencontainers.image.revision=$SOURCE_COMMIT
LABEL org.opencontainers.image.source="https://github.com/sharpninja/valhalla-dotnet"
LABEL io.sharpninja.valhalla.upstream.version="3.8.3"
LABEL io.sharpninja.valhalla.upstream.commit="a60c7cbfc83e073f50887cd27e0109d02e6b64e5"

WORKDIR /app
COPY --from=build --chown=app:app /app/publish/ /app/
USER root
RUN mkdir -p /input /work /output \
    && chown -R app:app /input /work /output
USER app

ENV PATH="/app:${PATH}"
ENV DOTNET_EnableDiagnostics=0
VOLUME ["/input", "/work", "/output"]
ENTRYPOINT ["valhalla-dotnet"]
