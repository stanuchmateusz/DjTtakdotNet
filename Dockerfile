# Build stage
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src

RUN apt-get update && \
    apt-get install -y --no-install-recommends git && \
    apt-get clean && \
    rm -rf /var/lib/apt/lists/*

RUN git clone https://github.com/stanuchmateusz/DjTtakdotNet.git .
WORKDIR /src/DjTtakdotNet

RUN dotnet restore
RUN dotnet publish -c Release -o /app

# Runtime stage with ASP.NET + native deps + yt-dlp
FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app

# Install yt-dlp and runtime dependencies
RUN apt-get update && \
    apt-get install -y --no-install-recommends \
    ffmpeg \
    python3 \
    libopus0 \
    libsodium23 \
    libsodium-dev \
    curl \
    libglib2.0-0 \
    libx11-6 && \
    ln -s /usr/lib/x86_64-linux-gnu/libopus.so.0 /usr/lib/x86_64-linux-gnu/libopus.so && \
    curl -L https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp -o /usr/local/bin/yt-dlp && \
    chmod a+rx /usr/local/bin/yt-dlp && \
    apt-get clean && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/ ./
CMD ["dotnet", "DjTtakdotNet.dll"]