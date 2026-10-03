# Agent Orchestrator + the coding-agent CLIs in one image.
# Pin CLI versions (see docs: JSON output formats change between releases).
#   docker build -t agent-orchestrator \
#     --build-arg CLAUDE_CODE_VERSION=2.1.288 --build-arg CODEX_VERSION=<version> --build-arg OPENCODE_VERSION=1.18.34 --build-arg QODER_VERSION=1.1.65 .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY Directory.Build.props Orchestrator.slnx ./
COPY src/ src/
RUN dotnet publish src/Orchestrator.Api -c Release -o /out/api \
 && dotnet publish src/Orchestrator.Cli -c Release -o /out/cli

FROM mcr.microsoft.com/dotnet/aspnet:10.0
ARG NODE_MAJOR=22
ARG CLAUDE_CODE_VERSION=latest
ARG CODEX_VERSION=latest
ARG OPENCODE_VERSION=latest
ARG QODER_VERSION=latest

RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates curl bash ripgrep \
 && curl -fsSL https://deb.nodesource.com/setup_${NODE_MAJOR}.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install -g --no-audit --no-fund \
      @anthropic-ai/claude-code@${CLAUDE_CODE_VERSION} \
      @openai/codex@${CODEX_VERSION} \
      opencode-ai@${OPENCODE_VERSION} \
      @qoder-ai/qodercli@${QODER_VERSION} \
 && npm cache clean --force \
 && rm -rf /var/lib/apt/lists/*

# Run as an unprivileged user that owns the agent credential directories.
RUN useradd --create-home --uid 1000 agent \
 && mkdir -p /data /repos && chown agent:agent /data /repos
USER agent
WORKDIR /home/agent

COPY --from=build --chown=agent:agent /out/api /app
COPY --from=build --chown=agent:agent /out/cli /opt/orch
RUN printf '#!/bin/sh\nexec dotnet /opt/orch/orch.dll "$@"\n' > /home/agent/orch && chmod +x /home/agent/orch
ENV PATH="/home/agent:${PATH}" \
    ASPNETCORE_URLS=http://0.0.0.0:7777 \
    Orchestrator__DataDirectory=/data \
    Orchestrator__AllowedRepositoryRoots__0=/repos \
    OPENCODE_DISABLE_AUTOUPDATE=1 \
    DISABLE_AUTOUPDATER=1

EXPOSE 7777
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "/app/Orchestrator.Api.dll"]
