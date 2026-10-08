# Agent Orchestrator

A small, self-hosted .NET service that lets one coding agent **delegate a subtask to another**:
Claude Code → Codex, Claude Code → OpenCode, Claude Code → Qoder, OpenCode → Claude Code, and so on. The orchestrator runs the
other agent's real CLI (with its own login), in an **isolated git worktree**, streams what it is doing,
relays its questions back, and returns a branch + diff + test result that the caller merges.

```text
Claude Code ──MCP (Streamable HTTP) / `orch` CLI──► Orchestrator (.NET 10) ──child process──► codex / opencode / qoder / claude
     ▲                                                   │  git worktree per task, SQLite state,
     └────── task result: summary, branch, diff, tests ◄─┘  live event stream, dashboard
```

Background research and design rationale: [docs/research/multi-agent-orchestration.md](docs/research/multi-agent-orchestration.md).

## What it does

- **Delegate**: `delegate_task(agent, prompt, repo_path, test_command?)` returns immediately with a task id. The calling agent
  asks you which platform to use (see *Using it*).
- **Isolate**: each task gets `orchestrator/<task-id>` in `<repo>/.orchestrator/worktrees/<task-id>`, branched
  from your HEAD **plus your uncommitted changes** (snapshotted without touching your index or files).
- **Run**: the agent's CLI runs headless: `claude -p --output-format stream-json`, `codex exec --json`,
  `opencode run --format json`, `qodercli -p --output-format stream-json`. The prompt goes in on stdin, and output is parsed into one common event format.
- **Ask back**: if the agent needs a decision it ends with `NEEDS_INPUT: <question>`. The task becomes
  `input_required`. Your `answer_task` resumes **the same agent session** (`--resume` / `exec resume` / `--session`).
- **Finish**: the orchestrator commits leftover changes, records the diff and patch, runs your test command, and returns
  `completed` with `nextStep` instructions: `git merge --no-ff <branch>`, or `git apply <patch>` when the task started from your
  uncommitted work (merging that snapshot would conflict with your own copy), then `cleanup_task`. Cleanup deletes the branch only
  once its changes are in your tree, unless you pass `force`.
- **Tests are verified independently**: the orchestrator runs your `test_command` itself (Git Bash on Windows when installed,
  so `grep`/`test -f` work). A failure is flagged (`needsAttention`, an amber "tests failed" chip) even if the agent claims success.
- **Survive restarts**: all state is in SQLite + JSONL event logs. A fresh Claude session can `list_tasks`.

## Which model is used?

Each task shows the model in the dashboard (task list, header and a **Model** row), in `orch get`/`orch list` (`modelUsed`) and as a
`model: …` line in the live feed:

| Agent | Where the model comes from |
|---|---|
| Claude Code, Qoder | Reported live by the agent's own output (the model that actually answered; Qoder's `auto` is replaced by the concrete model when it reports one) |
| OpenCode | Read after each turn from `opencode export <session>` (`provider/model`) |
| Codex | The requested model, a reroute notice from Codex, or your `~/.codex/config.toml` default (Codex's JSON output doesn't name its model) |

Pick a model per task with `model` (MCP / API), `--model` (`orch`), or the New task form. Set a per-agent default with
`Orchestrator:Agents:<name>:Model`. If you request a model and the agent reports a different one, the dashboard shows both.

## Can I see what a delegated agent is doing?

Yes. A headless child process has no window of its own, so the orchestrator makes the work visible in four ways:

| Way | What you see | Works for |
|---|---|---|
| **Dashboard**, http://127.0.0.1:7777/ | Live feed of every task: agent messages, shell commands, file edits, errors, test output, plus the agent's question with an answer box, diff stat and patch download | All agents |
| **`orch watch <task-id>`** | The same live feed, colour-coded in your terminal | All agents |
| **Real TUI, attached live** | Set `Orchestrator:Agents:opencode:AttachUrl` to a running `opencode serve` (e.g. `http://127.0.0.1:4096`). Tasks then run *inside* that server, and `opencode attach http://127.0.0.1:4096 --session <id>` (shown as `watchCommand`) opens OpenCode's own UI on the live session | OpenCode |
| **Open the session afterwards** | `watchCommand`, e.g. `claude --resume <id>`, `codex resume <id>` or `qodercli --resume <id>` in the task's worktree, opens the full conversation in the agent's own UI | Claude Code, Codex, Qoder |

You can also open the task's worktree folder in your editor while it runs: the files change on disk as the agent works.
Claude Code and Codex have no supported way to attach a UI to a running headless run, so for those the
dashboard and `orch watch` are the live view.

## Install

Prerequisites: [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0), git, and at least one agent CLI, installed and
logged in as usual:

| Platform | Install | Log in |
|---|---|---|
| Claude Code | `npm i -g @anthropic-ai/claude-code` | `claude`, then `/login` |
| Codex | `npm i -g @openai/codex` | `codex login` |
| OpenCode | `npm i -g opencode-ai` | `opencode auth login` |
| Qoder | Qoder's installer (PowerShell `irm https://qoder.com/install.ps1 \| iex`) or `npm i -g @qoder-ai/qodercli` | `qodercli`, then `/login` |

Then, from a clone of this repository:

```powershell
.\install.ps1          # Windows (if scripts are blocked: powershell -ExecutionPolicy Bypass -File .\install.ps1)
```
```bash
./install.sh           # Linux / macOS
```

The installer:
1. builds a self-contained app into `%LOCALAPPDATA%\agent-orchestrator\app` (Linux `~/.local/share/agent-orchestrator/app`,
   macOS `~/Library/Application Support/agent-orchestrator/app`),
2. puts `orch` on your PATH (Windows: user PATH, so open a new terminal; Linux/macOS: `~/.local/bin/orch`),
3. runs **`orch setup`**, which lists the agent CLIs it found and asks, one by one, which to connect:

```text
Agent CLIs on this computer:
  ✔ Claude Code    C:\Users\you\AppData\Roaming\npm\claude.cmd
  ✘ Codex          not installed — npm i -g @openai/codex, then `codex login`
  ✔ OpenCode       C:\Users\you\AppData\Roaming\npm\opencode.cmd
  ✔ Qoder          C:\Users\you\.qoder\bin\qodercli\qodercli.exe

Connect Claude Code? [Y/n]
Connect OpenCode? [Y/n]
Connect Qoder? [Y/n]
Start the orchestrator automatically when you log in? [Y/n]
```

**Connecting** an agent registers the orchestrator's MCP server with that agent (user-wide, so it works in every project) and installs
the `agent-orchestrator` skill into the agent's skills folder. After that you can delegate from inside that agent.
**Autostart** uses Task Scheduler on Windows, a systemd user service on Linux and a LaunchAgent on macOS.

- **Upgrade:** `git pull`, then run the installer again. Settings and task history are kept.
- **Add an agent you installed later:** `orch setup` again, or **Connect** on the Setup page.
- **Uninstall:** `.\install.ps1 -Uninstall` / `./install.sh --uninstall` disconnects every agent, removes autostart and the app.
  Task history and settings stay in the data folder unless you add `-Purge` / `--purge`.
- **Non-interactive:** `orch setup --agents claude,qoder --yes [--no-autostart]`
  (installer: `.\install.ps1 -SetupArgs '--agents','claude,qoder','--yes'`, `./install.sh --agents claude,qoder --yes`).

## Web pages (UI)

With the server running (it starts at login, or run `orch start`):

| Page | Address | What it's for |
|---|---|---|
| **Dashboard** | **http://127.0.0.1:7777/** | All tasks, live activity of each, answer an agent's question, follow up, download the patch, start a task by hand |
| **Setup** | **http://127.0.0.1:7777/#setup** | Platforms: installed?, connected?, **Connect / Disconnect / Check**, default model, permission policy, enable/disable. **Add a platform.** Server addresses, data folder, settings file, **Start at login** switch |
| One task | http://127.0.0.1:7777/#task=&lt;task-id&gt; | Direct link to a task (every task result includes it as `dashboardUrl`) |
| MCP endpoint | http://127.0.0.1:7777/mcp | For agents, not a web page. Setup registers this address |

`orch open` opens the dashboard, `orch open setup` the Setup page and `orch open <task-id>` a task. `orch status` prints these
addresses. If you change the port (`"Urls": "http://127.0.0.1:8080"` in your settings file), every printed link, the
MCP registration and the skill follow the configured address. Re-run `orch setup` afterwards so the agents learn the new URL.

```text
orch setup | start | stop | status | open [setup|<task-id>] | uninstall
```

## Using it

In any Git repository, ask a connected agent to hand work over, e.g. in Claude Code:
*"Delegate adding input validation to the signup endpoint to another agent, with `dotnet test` as the check, and continue with
the docs while it runs."*

1. **It asks you which platform.** Unless you named one ("…to Codex"), the agent calls `list_agents` and asks you to choose
   among the platforms that are installed and enabled, showing each one's default model. It never picks silently: a
   `delegate_task` call without `agent` starts nothing and returns `agent_selection_required` with the available choices
   (and install hints for missing ones).
2. It splits the request into small subtasks (one concern, a few files, a clear check each) and calls `delegate_task` once
   per subtask, in parallel when they're independent. It keeps working and polls `wait_task`. Watch each task live on the dashboard.
3. If the delegated agent asks something, your agent answers it or relays the question to you.
4. When the task is done it reviews the diff and test result and merges the branch.

It works the same from Codex, OpenCode, Qoder or any connected platform, e.g. in `qodercli`: *"Use the orchestrator to delegate
to opencode: …"*.

### Which projects can it work on?

Any Git repository on this machine. One running orchestrator serves all of them: every task names its repository
(`repo_path`, normally the calling agent's current directory) and gets its own worktree inside that repo, so tasks in different
projects never mix. A repository needs at least one commit (for a new folder: `git init && git add -A && git commit -m init`).

- Restrict it to certain folders with `Orchestrator:AllowedRepositoryRoots` (e.g. `["C:/src", "D:/work"]`).
- `Orchestrator:DefaultRepository` is used when a caller doesn't pass a path.
- Task history lives in one per-user data folder (shown on the Setup page), so it doesn't matter where the server runs from.

### From a shell

Any agent (or you) can also use `orch`:

```bash
orch delegate codex "Add input validation to POST /signup; reject empty email" --test "dotnet test" --wait
orch watch <task-id>                 # live activity
orch answer <task-id> "Use FluentValidation"
orch continue <task-id> "Also cover the PUT endpoint"
orch cleanup <task-id> --delete-branch
```

`orch wait` / `delegate --wait` exit codes: `0` completed, `2` input required, `1` failed/cancelled, `3` still running.

## Commands delegated agents may run

By default delegated agents may run **any `dotnet` command** (`dotnet build`, `dotnet test`, `dotnet restore`, …), and the task's
own `test_command` is always allowed too. The task brief tells the agent these are pre-approved and asks it to build and test before
finishing.

| Agent | What happens to shell commands |
|---|---|
| Claude Code, Qoder | Headless runs refuse every command that isn't pre-approved. The allowed commands are passed as `--allowed-tools` rules for both the `Bash` and the Windows `PowerShell` tool (e.g. `Bash(dotnet *)`) |
| OpenCode | All commands allowed except risky git ones (`OPENCODE_PERMISSION` in its defaults) |
| Codex | Runs commands in its `workspace-write` sandbox. Network access inside the sandbox is on by default (`-c sandbox_workspace_write.network_access=true`) so `dotnet restore` / `npm install` can download packages |

Change the list on the **Setup page → "Commands delegated agents may run"** (one prefix per line, e.g. `dotnet`, `npm test`,
`pytest`), or set `Orchestrator:AllowedCommands` in your settings file (`[""]` = none). Changes apply to the next agent turn.
If you replaced an agent's **Permission policy / extra arguments** yourself, its defaults (such as Codex's network setting) no
longer apply; use **Reset arguments to default** on its Setup card.

## Adding a platform

**New project:** nothing to do. Any Git repository works.

**New agent CLI** (e.g. Gemini CLI, Cursor Agent, GitHub Copilot CLI): open **Setup → Add a platform**
(http://127.0.0.1:7777/#setup), or add it to your settings file. No code is needed:

```jsonc
"Orchestrator": { "Agents": { "gemini": {
  "DisplayName": "Gemini CLI",
  "Executable": "gemini",
  "Protocol": "claude-stream-json",          // claude-stream-json | codex-jsonl | opencode-json | text
  "Args": ["-p", "--output-format", "stream-json"],
  "ModelArgs": ["--model", "{model}"],
  "ResumeArgs": ["--resume", "{sessionId}"], // omit if the CLI can't resume: answers then restate the task as a new turn
  "PromptVia": "stdin",                      // or "arg" (appended, or wherever "{prompt}" appears in Args)
  "ExtraArgs": ["--yolo"],                   // permission policy
  "Integration": {                           // what Connect does, so this agent can delegate too (optional)
    "McpAdd": ["gemini", "mcp", "add", "orchestrator", "{url}"],
    "McpRemove": ["gemini", "mcp", "remove", "orchestrator"],
    "SkillsDirs": ["~/.gemini/skills"]
  } } } }
```

- **Protocols:** `claude-stream-json` gives the full live feed and model/session detection for CLIs that print Claude Code-style
  `stream-json`; `codex-jsonl` and `opencode-json` likewise. **`text` works with any CLI**: each output line appears in the live
  feed and the whole output is the final message (no session resume).
- The flags above are only an example: check them against the CLI's `--help`, then use **Check** on the Setup page (runs
  `--version`, or your `VersionArgs`; no model call).
- A CLI with a brand-new output format needs a small parser in code; see *Development*.

## Settings

Your settings live in `config.json` in the data folder (`%LOCALAPPDATA%\agent-orchestrator\config.json`; Linux
`~/.local/share/agent-orchestrator/config.json`; macOS `~/Library/Application Support/agent-orchestrator/config.json`). The
Setup page writes it for you and changes apply without a restart. It uses the same shape as
`src/Orchestrator.Api/appsettings.json`, which holds the defaults and is replaced on upgrade. Environment variables
(`Orchestrator__MaxConcurrentTasks=4`) override both.

The server log is `logs/server.log` in the data folder (`Orchestrator:LogFile` changes it; `"off"` disables it).

### Windows notes

npm installs each CLI as `opencode.cmd` (plus an extensionless bash shim that Windows can't run). The orchestrator picks the
`.cmd`/`.exe` automatically and runs it via `cmd.exe /c`. If a CLI lives somewhere else, set its **Program** on the Setup page
(`Orchestrator:Agents:<name>:Executable`). In the orchestrator, Qoder is called **`qoder`**; the program it runs is
**`qodercli`** (`qodercli.exe` from the installer). CLIs installed after the server started are found too. Delegating to
an agent whose CLI can't be found is rejected immediately with install instructions.

## MCP tools

| Tool | Purpose |
|---|---|
| `delegate_task` | Start a task (prompt, agent (without it: returns the choices to ask the user, starts nothing), repo_path, base_ref, include_uncommitted, model, test_command, timeout_minutes, parent_task_id, client_request_id) |
| `wait_task` | Long-poll ≤ 90 s; returns early on completed / failed / cancelled / **input_required** |
| `answer_task` | Answer the agent's question; resumes the same session |
| `continue_task` | Follow-up instruction to a finished task (same session, same worktree) |
| `get_task`, `list_tasks` | State; recover task ids after a restart or compaction |
| `get_task_events` | Live progress (incremental with `after_seq`) |
| `cancel_task`, `cleanup_task` | Kill the process tree / remove the worktree (and branch) |
| `list_agents` | Available platforms with default models, and missing ones with install hints |

Every result carries `nextStep`, so the calling model always knows what to do next.

## HTTP API

`POST /api/tasks`, `GET /api/tasks[?status=&parent=&limit=]`, `GET /api/tasks/{id}`, `GET /api/tasks/{id}/wait?timeoutSeconds=`,
`POST /api/tasks/{id}/answer|continue|cancel` (`{"message": "..."}`), `DELETE /api/tasks/{id}/worktree?deleteBranch=`,
`GET /api/tasks/{id}/patch`, `GET /api/tasks/{id}/events` (Server-Sent Events; `?follow=false` for JSON), `GET /api/agents`, `GET /health`.

Setup (used by the Setup page and `orch setup`): `GET /api/setup/info`, `GET /api/setup/platforms[/{name}]`,
`POST /api/setup/platforms/{name}/connect|disconnect|check`, `PUT /api/setup/platforms/{name}` (`enabled`, `model`, `executable`,
`extraArgs`), `POST /api/setup/platforms` (add a custom platform), `DELETE /api/setup/platforms/{name}`,
`POST /api/setup/autostart` (`{"enabled": true}`), `POST /api/admin/shutdown`.

Requests that change something (`POST`/`PUT`/`DELETE` under `/api`) must send an `X-Orchestrator-Client: <any name>` header. The
dashboard and `orch` do this. It stops web pages on other sites from driving the server through your browser.

## Configuration reference (settings file, see *Settings*)

| Setting | Default | Notes |
|---|---|---|
| `Urls` | `http://127.0.0.1:7777` | Top-level key. Keep it on localhost unless you set `ApiKey` |
| `Orchestrator:TrustedHosts` | `[]` | Host names accepted besides localhost/127.0.0.1 (e.g. a Docker service name); others get 400 |
| `Orchestrator:DataDirectory`, `LogFile` | per-user folder | Task database, event logs, patches; server log |
| `Orchestrator:ApiKey` | none | Required as `X-Orchestrator-Key`, `Authorization: Bearer`, or `?key=` |
| `Orchestrator:AllowedRepositoryRoots` | any | Restrict which repos can be targeted |
| `Orchestrator:DefaultRepository` | none | Used when `repo_path` is omitted |
| `Orchestrator:MaxConcurrentTasks` / `Agents:<name>:MaxConcurrent` | 3 / 2 | Subscription plans share rate limits across parallel runs |
| `Orchestrator:DefaultTimeoutMinutes` / `IdleTimeoutMinutes` | 60 / 15 | Hard limit and no-output watchdog (kills the whole process tree) |
| `Orchestrator:MaxDepth` | 2 | Stops delegation loops (A → B → A …) |
| `Orchestrator:AllowedCommands` | `["dotnet"]` | Commands delegated agents may run (see *Commands delegated agents may run*) |
| `Orchestrator:CopyIntoWorktree` | `[]` | Gitignored files a worktree needs, e.g. `.env.local` |
| `Orchestrator:ScrubEnvironmentVariables` | session/IPC vars | Stops children from inheriting the parent agent's session identity (see below) |
| `Agents:<name>:ExtraArgs` | built in | **Permission policy per agent** (`--permission-mode acceptEdits`, `--sandbox workspace-write` + network, `--auto` + `OPENCODE_PERMISSION` deny rules, Qoder `--permission-mode accept_edits`) |
| `Agents:<name>:Model`, `Executable`, `Environment`, `Enabled` | | Model default, binary path, extra env (e.g. API keys), hide from delegation |
| `Agents:<name>:Protocol`, `Args`, … | | Custom platforms (see *Adding a platform*) |
| `Agents:opencode:AttachUrl` | none | Run OpenCode tasks inside a shared `opencode serve` so you can attach live |

## Authentication

The orchestrator never handles credentials. It starts the **unmodified** CLIs as your user, and each one uses its own login:

- **Claude Code**: your `/login` subscription session, `CLAUDE_CODE_OAUTH_TOKEN` (from `claude setup-token`), or `ANTHROPIC_API_KEY`.
  Anthropic's terms allow signing in to the unmodified Claude Code binary with your own subscription. For heavy unattended
  fan-out, prefer an API key. Do **not** use a Claude subscription inside OpenCode (Anthropic prohibits it).
- **Codex**: ChatGPT login (`codex login`, or `--device-auth` for headless) or `CODEX_API_KEY` / API-key login. Local models: `--oss`.
- **Qoder**: your `qodercli` → `/login` session, or `QODER_PERSONAL_ACCESS_TOKEN` (a personal access token from Qoder Integrations) for
  headless/CI/Docker. The China edition installs as `qoderclicn`: set `Orchestrator:Agents:qoder:Executable` to it.
- **OpenCode**: whatever `opencode auth login` configured: API keys, ChatGPT Plus/Pro, GitHub Copilot, Ollama/LM Studio, any OpenAI-compatible endpoint.

## Docker

```bash
REPOS_DIR=$HOME/src docker compose up -d --build
docker compose exec orchestrator claude          # /login once (or set CLAUDE_CODE_OAUTH_TOKEN / ANTHROPIC_API_KEY)
docker compose exec orchestrator codex login --device-auth
docker compose exec orchestrator opencode auth login
docker compose exec orchestrator qodercli         # /login once (or set QODER_PERSONAL_ACCESS_TOKEN)
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
- **The server only accepts local callers**: it rejects unknown `Host` headers and foreign `Origin`s, and state-changing calls need
  the `X-Orchestrator-Client` header, so a website you visit can't use it. Set `ApiKey` if other users share the machine.
- **Setup edits agent configs**: Connect runs each agent's own `mcp add` command, or (OpenCode) rewrites `opencode.json`
  after saving `opencode.json.bak` (comments in that file are not preserved). Disconnect removes only what Connect added, and
  never deletes a skill file you edited.
- Treat a delegated agent's output as untrusted data: review the diff before merging; nothing is merged or pushed automatically.

## Development

```bash
dotnet build && dotnet test                         # unit, API and end-to-end tests
dotnet run --project src/Orchestrator.Api           # dev server on http://127.0.0.1:7777 (stop the installed one first: orch stop)
dotnet run --project src/Orchestrator.Cli -- status
```

A development server uses the same data folder and settings file as the installed app. Autostart can only be enabled for
the installed app.

### Manual setup (without the installer)

What Connect does for each built-in platform, if you'd rather do it by hand (use your own address if you changed the port):

| Platform | Register the MCP server (user-wide) | Skill folder (copy `skills/agent-orchestrator` into it) |
|---|---|---|
| Claude Code | `claude mcp add --scope user --transport http orchestrator http://127.0.0.1:7777/mcp` | `~/.claude/skills/` |
| Codex | `codex mcp add orchestrator --url http://127.0.0.1:7777/mcp` | `~/.codex/skills/` |
| Qoder | `qodercli mcp add --scope user --transport http orchestrator http://127.0.0.1:7777/mcp` | `~/.qoder/skills/` |
| OpenCode | add `"mcp": { "orchestrator": { "type": "remote", "url": "http://127.0.0.1:7777/mcp", "enabled": true } }` to `~/.config/opencode/opencode.json` | `~/.config/opencode/skills/` |

`--scope user` matters for Claude Code and Qoder: without it the server is only visible in the folder where you ran the command.
With `Orchestrator:ApiKey` set, add the key as an `X-Orchestrator-Key` header (Codex: `--bearer-token-env-var ORCHESTRATOR_API_KEY`).

Layout: `src/Orchestrator.Core` (adapters, parsers, worktrees, runner, store, event log), `src/Orchestrator.Api`
(HTTP + SSE + MCP + dashboard), `src/Orchestrator.Cli` (`orch`), `tests/Orchestrator.Tests`.

Adding an agent with a new output format: implement `IAgentAdapter` (build the command line, parse its output into
`ParsedEvent`s, extract the session id and final message), register it in `Program.cs`, and add its connect commands to
`Setup/PlatformIntegrations.cs`. For CLIs that reuse an existing format, a config entry is enough (*Adding a platform*).

Layout additions: `src/Orchestrator.Core/Setup` (connect/disconnect, skill install, OpenCode config edits, autostart),
`src/Orchestrator.Api/SetupEndpoints.cs`, `skills/agent-orchestrator/SKILL.md` (embedded into the app), `install.ps1` / `install.sh`.

## Known limitations (v1)

- Questions use the text convention `NEEDS_INPUT:` and are answered by **resuming** the session (a new turn). In-place
  pausing on tool-permission prompts (OpenCode server `permission.asked`, Codex app-server `requestApproval`,
  Claude `--permission-prompt-tool`) is the planned v1.5 adapter work. Until then, permissions are decided by each agent's configured policy.
- The Docker image has not been build-tested in CI yet.
