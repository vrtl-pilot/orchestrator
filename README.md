# Agent Orchestrator

A small, self-hosted .NET service that lets one coding agent **delegate a subtask to another**:
Claude Code → Codex, Claude Code → OpenCode, OpenCode → Claude Code, and so on. The orchestrator runs the
other agent's real CLI (with its own login), in an **isolated git worktree**, streams what it is doing,
relays its questions back, and returns a branch + diff + test result that the caller merges.

```text
Claude Code ──MCP (Streamable HTTP) / `orch` CLI──► Orchestrator (.NET 10) ──child process──► codex / opencode / claude
     ▲                                                   │  git worktree per task, SQLite state,
     └────── task result: summary, branch, diff, tests ◄─┘  live event stream, dashboard
```

Background research and design rationale: [docs/research/multi-agent-orchestration.md](docs/research/multi-agent-orchestration.md).

## What it does

- **Delegate**: `delegate_task(agent, prompt, repo_path, test_command?)` returns immediately with a task id.
- **Isolate**: each task gets `orchestrator/<task-id>` in `<repo>/.orchestrator/worktrees/<task-id>`, branched
  from your HEAD **plus your uncommitted changes** (snapshotted without touching your index or files).
- **Run**: the agent's CLI runs headless: `claude -p --output-format stream-json`, `codex exec --json`,
  `opencode run --format json`. The prompt goes in on stdin, and output is parsed into one common event format.
- **Ask back**: if the agent needs a decision it ends with `NEEDS_INPUT: <question>`. The task becomes
  `input_required`. Your `answer_task` resumes **the same agent session** (`--resume` / `exec resume` / `--session`).
- **Finish**: the orchestrator commits leftover changes, records the diff and patch, runs your test command, and returns
  `completed` with `nextStep` instructions (`git merge --no-ff <branch>`, then `cleanup_task`).
- **Survive restarts**: all state is in SQLite + JSONL event logs. A fresh Claude session can `list_tasks`.

## Can I see what a delegated agent is doing?

Yes. A headless child process has no window of its own, so the orchestrator makes the work visible in four ways:

| Way | What you see | Works for |
|---|---|---|
| **Dashboard**, `http://127.0.0.1:7777` | Live feed of every task: agent messages, shell commands, file edits, errors, test output, plus the agent's question with an answer box, diff stat and patch download | All agents |
| **`orch watch <task-id>`** | The same live feed, colour-coded in your terminal | All agents |
| **Real TUI, attached live** | Set `Orchestrator:Agents:opencode:AttachUrl` to a running `opencode serve` (e.g. `http://127.0.0.1:4096`). Tasks then run *inside* that server, and `opencode attach http://127.0.0.1:4096 --session <id>` (shown as `watchCommand`) opens OpenCode's own UI on the live session | OpenCode |
| **Open the session afterwards** | `watchCommand`, e.g. `claude --resume <id>` or `codex resume <id>` in the task's worktree, opens the full conversation in the agent's own UI | Claude Code, Codex |

You can also open the task's worktree folder in your editor while it runs: the files change on disk as the agent works.
Claude Code and Codex have no supported way to attach a UI to a running headless run, so for those the
dashboard and `orch watch` are the live view.

## Quick start (Linux / macOS / WSL / Windows)

Prerequisites: .NET 10 SDK, git, and the agent CLIs you want to use (installed and logged in as usual):
`npm i -g @anthropic-ai/claude-code @openai/codex opencode-ai`, then `claude` → `/login`, `codex login`,
`opencode auth login`.

```bash
dotnet run --project src/Orchestrator.Api          # http://127.0.0.1:7777 (dashboard), /mcp (MCP)
dotnet run --project src/Orchestrator.Cli -- agents # shows which agent CLIs were found
```

Install the CLI as a tool (optional): `dotnet pack src/Orchestrator.Cli -o nupkg && dotnet tool install -g Orchestrator.Cli --add-source nupkg`.

### Connect Claude Code

```bash
claude mcp add --transport http orchestrator http://127.0.0.1:7777/mcp
mkdir -p ~/.claude/skills && cp -r examples/claude-code/skills/delegate ~/.claude/skills/
```

Then, in any repo: *"Delegate adding input validation to the signup endpoint to Codex, with `dotnet test` as the check,
and continue with the docs while it runs."* Claude calls `delegate_task`, keeps working, polls `wait_task`, relays any
question to you, and merges the branch when it is done.

### Connect Codex or OpenCode (so they can delegate too)

```bash
codex mcp add orchestrator --url http://127.0.0.1:7777/mcp
```

```jsonc
// opencode.json
{ "mcp": { "orchestrator": { "type": "remote", "url": "http://127.0.0.1:7777/mcp", "enabled": true } } }
```

Any agent (or you) can also use the CLI from a shell:

```bash
orch delegate codex "Add input validation to POST /signup; reject empty email" --test "dotnet test" --wait
orch watch <task-id>                 # live activity
orch answer <task-id> "Use FluentValidation"
orch continue <task-id> "Also cover the PUT endpoint"
orch cleanup <task-id> --delete-branch
```

`orch wait` / `delegate --wait` exit codes: `0` completed, `2` input required, `1` failed/cancelled, `3` still running.

## MCP tools

| Tool | Purpose |
|---|---|
| `delegate_task` | Start a task (agent, prompt, repo_path, base_ref, include_uncommitted, model, test_command, timeout_minutes, parent_task_id, client_request_id) |
| `wait_task` | Long-poll ≤ 90 s; returns early on completed / failed / cancelled / **input_required** |
| `answer_task` | Answer the agent's question; resumes the same session |
| `continue_task` | Follow-up instruction to a finished task (same session, same worktree) |
| `get_task`, `list_tasks` | State; recover task ids after a restart or compaction |
| `get_task_events` | Live progress (incremental with `after_seq`) |
| `cancel_task`, `cleanup_task` | Kill the process tree / remove the worktree (and branch) |
| `list_agents` | Which agent CLIs are installed |

Every result carries `nextStep`, so the calling model always knows what to do next.

## HTTP API

`POST /api/tasks`, `GET /api/tasks[?status=&parent=&limit=]`, `GET /api/tasks/{id}`, `GET /api/tasks/{id}/wait?timeoutSeconds=`,
`POST /api/tasks/{id}/answer|continue|cancel` (`{"message": "..."}`), `DELETE /api/tasks/{id}/worktree?deleteBranch=`,
`GET /api/tasks/{id}/patch`, `GET /api/tasks/{id}/events` (Server-Sent Events; `?follow=false` for JSON), `GET /api/agents`, `GET /health`.

## Configuration (`src/Orchestrator.Api/appsettings.json`, or env vars like `Orchestrator__MaxConcurrentTasks=4`)

| Setting | Default | Notes |
|---|---|---|
| `Urls` | `http://127.0.0.1:7777` | Keep it on localhost unless you set `ApiKey` |
| `Orchestrator:ApiKey` | none | Required as `X-Orchestrator-Key`, `Authorization: Bearer`, or `?key=` |
| `Orchestrator:AllowedRepositoryRoots` | any | Restrict which repos can be targeted |
| `Orchestrator:DefaultRepository` | none | Used when `repo_path` is omitted |
| `Orchestrator:MaxConcurrentTasks` / `Agents:<name>:MaxConcurrent` | 3 / 2 | Subscription plans share rate limits across parallel runs |
| `Orchestrator:DefaultTimeoutMinutes` / `IdleTimeoutMinutes` | 60 / 15 | Hard limit and no-output watchdog (kills the whole process tree) |
| `Orchestrator:MaxDepth` | 2 | Stops delegation loops (A → B → A …) |
| `Orchestrator:CopyIntoWorktree` | `[]` | Gitignored files a worktree needs, e.g. `.env.local` |
| `Orchestrator:ScrubEnvironmentVariables` | session/IPC vars | Stops children from inheriting the parent agent's session identity (see below) |
| `Agents:<name>:ExtraArgs` | see file | **Permission policy per agent** (`--permission-mode acceptEdits`, `--sandbox workspace-write`, `--auto` + `OPENCODE_PERMISSION` deny rules) |
| `Agents:<name>:Model`, `Executable`, `Environment` | | Model default, binary path, extra env (e.g. API keys) |
| `Agents:opencode:AttachUrl` | none | Run OpenCode tasks inside a shared `opencode serve` so you can attach live |

## Authentication

The orchestrator never handles credentials. It starts the **unmodified** CLIs as your user, and each one uses its own login:

- **Claude Code**: your `/login` subscription session, `CLAUDE_CODE_OAUTH_TOKEN` (from `claude setup-token`), or `ANTHROPIC_API_KEY`.
  Anthropic's terms allow signing in to the unmodified Claude Code binary with your own subscription. For heavy unattended
  fan-out, prefer an API key. Do **not** use a Claude subscription inside OpenCode (Anthropic prohibits it).
- **Codex**: ChatGPT login (`codex login`, or `--device-auth` for headless) or `CODEX_API_KEY` / API-key login. Local models: `--oss`.
- **OpenCode**: whatever `opencode auth login` configured: API keys, ChatGPT Plus/Pro, GitHub Copilot, Ollama/LM Studio, any OpenAI-compatible endpoint.

## Docker

```bash
REPOS_DIR=$HOME/src docker compose up -d --build
docker compose exec orchestrator claude          # /login once (or set CLAUDE_CODE_OAUTH_TOKEN / ANTHROPIC_API_KEY)
docker compose exec orchestrator codex login --device-auth
docker compose exec orchestrator opencode auth login
```

Repos are mounted at the **same absolute path** inside the container, so `repo_path` values sent by a Claude Code
running on the host resolve in the container. Credential directories live in named volumes (read-write, because the CLIs
write sessions and refresh tokens there). Pin CLI versions with the `*_VERSION` build args.

## Safety notes

- **A worktree is not a sandbox.** It shares the repo's `.git`, so an agent *could* run `git push` or touch other
  branches. The default per-agent policies deny the obvious commands. For untrusted work, run the orchestrator in Docker.
- **Repo config runs in headless agents**: `claude -p` loads the repo's `.claude/settings.json` hooks and `.mcp.json`
  without a trust prompt, and Codex/OpenCode load `AGENTS.md` / `opencode.json`. Only delegate on repositories you trust.
- **Parent-session isolation**: if the orchestrator is started from inside an agent session (e.g. from Claude Code's
  terminal), variables such as `CLAUDE_CODE_SESSION_ID` would make children reuse the parent's session and IPC channel.
  They are stripped by default (`ScrubEnvironmentVariables`).
- Treat a delegated agent's output as untrusted data: review the diff before merging; nothing is merged or pushed automatically.

## Development

```bash
dotnet build && dotnet test      # unit + end-to-end tests (git worktrees, a scripted fake agent, input-required flow)
```

Layout: `src/Orchestrator.Core` (adapters, parsers, worktrees, runner, store, event log), `src/Orchestrator.Api`
(HTTP + SSE + MCP + dashboard), `src/Orchestrator.Cli` (`orch`), `tests/Orchestrator.Tests`.

Adding an agent: implement `IAgentAdapter` (build the command line, parse its JSON output into `ParsedEvent`s, extract the
session id and final message), register it in `Program.cs`, and add an `Agents:<name>` section.

## Known limitations (v1)

- Questions use the text convention `NEEDS_INPUT:` and are answered by **resuming** the session (a new turn). In-place
  pausing on tool-permission prompts (OpenCode server `permission.asked`, Codex app-server `requestApproval`,
  Claude `--permission-prompt-tool`) is the planned v1.5 adapter work. Until then, permissions are decided by each agent's configured policy.
- The Docker image has not been build-tested in CI yet.
