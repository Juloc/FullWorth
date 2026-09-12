FROM node:22-bookworm-slim

ARG CODEX_VERSION=0.151.0
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates poppler-utils gosu \
    && rm -rf /var/lib/apt/lists/* \
    && npm install --global "@openai/codex@${CODEX_VERSION}" \
    && mv /usr/local/bin/codex /usr/local/bin/codex-real

WORKDIR /app
COPY src/FullWorth.CodexBridge/server.mjs ./server.mjs
COPY src/FullWorth.CodexBridge/codex-wrapper.mjs /usr/local/bin/codex
COPY src/FullWorth.CodexBridge/entrypoint.sh /usr/local/bin/fullworth-codex-entrypoint
RUN chmod 0755 /usr/local/bin/codex /usr/local/bin/fullworth-codex-entrypoint

# One secret, and only this one: this sidecar parses untrusted uploads, so it must never hold a key
# that establishes trusted user context. The deploy stack mounts exactly this file.
ENV NODE_ENV=production \
    PORT=8080 \
    BRIDGE_KEY_FILE=/run/fullworth-secrets/codex_bridge_key \
    CODEX_HOME=/data/codex \
    CODEX_WORKDIR=/tmp/codex-work

RUN mkdir -p /data/codex /tmp/codex-work \
    && chown -R node:node /data/codex /tmp/codex-work /app

EXPOSE 8080
ENTRYPOINT ["/usr/local/bin/fullworth-codex-entrypoint"]
CMD ["node", "server.mjs"]
