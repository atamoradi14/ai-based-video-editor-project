FROM node:22-bookworm-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg fonts-noto-core ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && mkdir -p /data/engine /tmp/studio \
    && chown -R node:node /data /tmp/studio
WORKDIR /engine
COPY video-engine/ai-edit-studio ./ai-edit-studio
COPY video-engine/bridge ./bridge
COPY video-engine/UPSTREAM_PIN.json ./UPSTREAM_PIN.json
USER node
ENV BRIDGE_BIND=0.0.0.0 \
    BRIDGE_PORT=32147 \
    GAME_TRAILER_STUDIO_PORT=32146 \
    GAME_TRAILER_STUDIO_DATA_ROOT=/data/engine \
    ENGINE_DATA_ROOT=/data/engine
EXPOSE 32147
HEALTHCHECK --interval=30s --timeout=3s --start-period=20s CMD node -e "fetch('http://127.0.0.1:32147/health').then(r=>process.exit(r.ok?0:1)).catch(()=>process.exit(1))"
CMD ["node", "bridge/server.mjs"]
