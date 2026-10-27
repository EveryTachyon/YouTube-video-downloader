
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build

WORKDIR /src

RUN dotnet new web \
    --name YouTubeDownloader \
    --output . \
    --framework net10.0 \
    --no-restore

RUN dotnet restore YouTubeDownloader.csproj

COPY Program.cs ./Program.cs

RUN dotnet publish YouTubeDownloader.csproj \
    --configuration Release \
    --output /out \
    --no-restore \
    /p:UseAppHost=false


FROM mcr.microsoft.com/dotnet/aspnet:10.0

ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1 \
    ASPNETCORE_URLS=http://0.0.0.0:8080 \
    PYTHONUNBUFFERED=1 \
    PYTHONDONTWRITEBYTECODE=1 \
    PIP_NO_CACHE_DIR=1


RUN apt-get update \
    && apt-get install -y --no-install-recommends \
        python3 \
        python3-pip \
        ffmpeg \
        curl \
        unzip \
        ca-certificates \
    && python3 -m pip install \
        --break-system-packages \
        --no-cache-dir \
        "yt-dlp[default]" \
    && rm -rf /var/lib/apt/lists/* \
    && test -x /usr/bin/ffmpeg \
    && yt-dlp --version

RUN curl -fsSL https://deno.land/install.sh | sh \
    && /root/.deno/bin/deno --version

ENV PATH="/root/.deno/bin:${PATH}"

WORKDIR /app

COPY --from=build /out/ ./
COPY wwwroot/ /app/wwwroot/

RUN test -f /app/YouTubeDownloader.dll \
    && mkdir -p /downloads


VOLUME ["/downloads"]

EXPOSE 8080

ENTRYPOINT ["dotnet", "/app/YouTubeDownloader.dll"]
