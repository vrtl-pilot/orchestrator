# Multi-agent coding orchestration: Claude Code ↔ OpenCode ↔ Codex

## Scope

How to build a self-hosted orchestrator so that one coding agent (e.g. Claude Code) can delegate a subtask to another (OpenCode, Codex CLI), wait for it, receive the result and code changes, and continue its own task.

Research date: 2026-10-03. Sources: code.claude.com docs (fetched live); the MCP spec repo (`modelcontextprotocol/modelcontextprotocol`, revisions 2025-11-25 and 2026-07-28); the OpenCode repo docs (`anomalyco/opencode`, `packages/web/src/content/docs/*.mdx`); the Codex repo (`openai/codex`: `codex-rs/exec/src/cli.rs`, `exec_events.rs`, `app-server/README.md`, `sdk/typescript/README.md`, `cli/src/main.rs`); GitHub repos and issues. opencode.ai, developers.openai.com and modelcontextprotocol.io were blocked by the network proxy here, so I read the same docs from their GitHub source. Facts I found only in secondary sources are marked *(secondary)*.

Labels: **[OFFICIAL]** officially supported and documented · **[DOCUMENTED-API]** possible by combining documented CLI/API features · **[UNOFFICIAL]** works through third-party or undocumented means · **[CUSTOM]** you would have to build it.

---

## 1. Short answers to the 12 critical questions

| # | Question | Answer |
|---|---|---|
| 1 | Can Claude Code delegate to OpenCode today? | **Yes, but not out of the box.** Nothing built in targets OpenCode. Claude can call an MCP tool you write, or run a CLI command through its Bash tool, and either one can start OpenCode. **[DOCUMENTED-API]** |
| 2 | Can OpenCode receive a task programmatically? | **Yes.** Three ways: `opencode run "<prompt>"`, the `opencode serve` HTTP API (`POST /session/:id/message` or `/prompt_async`), and the JS SDK. **[OFFICIAL]** |
| 3 | Can the orchestrator start OpenCode? | **Yes.** Start a process per task, or keep one `opencode serve` running and send it requests. **[OFFICIAL]** |
| 4 | Can it wait for OpenCode to finish? | **Yes.** The `opencode run` process exits when done. `POST /session/:id/message` blocks until the reply is ready. Or call `prompt_async` and watch SSE `/event` for `session.status` = idle. **[OFFICIAL]**, but see the 2026 SSE and `prompt_async` regressions (§4.4). |
| 5 | Can it capture the final response? | **Yes.** `--format json` gives JSON events. The message endpoint returns `{info, parts}`. `GET /session/:id/diff` returns the file diffs. **[OFFICIAL]** |
| 6 | Can it send the response back to Claude Code? | **Yes, as a tool result** (MCP tool result, or the stdout of a Bash command). Pushing an unsolicited message into a running session works only through Claude Code **channels** (research preview) or an `asyncRewake` hook. |
| 7 | Can Claude Code then continue automatically? | **Yes**, when the result arrives as the result of a tool call Claude made. An MCP call that runs longer than 2 minutes moves to the background, and Claude gets a task notification when it finishes. A background Bash command also notifies Claude when it ends. **[OFFICIAL]** The session has to stay alive: background tasks do not survive exiting it. |
| 8 | Same with Codex CLI? | **Yes.** Use `codex exec --json -o last.txt`, `codex exec resume <id>`, the Codex SDK (TS/Python), or `codex app-server` (JSON-RPC). There is also an official Claude Code plugin from OpenAI, `openai/codex-plugin-cc`. Note: `codex mcp-server` was **removed in Codex 0.154.0 (Sept 2026)**. **[OFFICIAL]** |
| 9 | Can several agents work at once? | **Yes**, as long as each task has its own git worktree (or clone). Pointing several writers at one directory is unsafe. |
| 10 | Safest way to share the repo? | **One git worktree and one branch per delegated task, created by the orchestrator.** The agent commits there. The orchestrator returns the branch name, diff and test results. The parent agent (or you) merges. |
| 11 | What do you have to build? | A small service: a task store, a worktree manager, an adapter per agent (process or HTTP), and a front door Claude can call (an MCP server and/or a CLI). Optionally, a channel or hook to push "done" events. **[CUSTOM]** |
| 12 | Existing projects? | Partial matches: **claw-orchestrator** (TS, MIT, the closest match), **openai/codex-plugin-cc** (Claude→Codex only), Claude Squad / Vibe Kanban / Orca / Conductor (they run agents side by side for a *human*, not agent-to-agent), ACP adapters. `coder/agentapi` was **archived on 2026-09-13**. None is .NET. |

**Bottom line:** every individual step is officially supported. The connecting piece (a "delegate task" service that owns worktrees and agent processes) does not exist in an official, .NET-friendly form. It is small enough to build yourself (a few thousand lines), and building it is reasonable.

---

## 2. MCP, precisely (not the same as agent-to-agent)

From the MCP spec, architecture section (rev. 2026-07-28):
- **Host** = the AI application (Claude Code). It creates **clients**, and each client talks to exactly **one server**.
- **Server** = a provider of tools, resources and prompts. Claude Code is the host/client. Your orchestrator would be an MCP **server**.
- **Who initiates:** the client sends requests (`tools/call` …). In 2025-11-25 a server could send requests back (sampling, elicitation, roots). In **2026-07-28 that changed**: the protocol is now *stateless* (no `initialize` handshake, no `Mcp-Session-Id`). Server→client requests are replaced by **MRTR**: the server replies `resultType: "input_required"` *within its reply* to a client request. **Sampling, Roots and Logging are deprecated.**
- **Bidirectional?** Only within a request/response exchange the client started, plus opted-in change notifications (`subscriptions/listen`). The protocol has **no "server sends a new task to the host" primitive.**
- **Can an MCP server start a task in Claude Code?** Not in standard MCP. The only way is Claude Code's own extension, **channels**: a server declares `capabilities.experimental['claude/channel']` and emits `notifications/claude/channel`. It is a research preview. Claude Code must be started with `--channels` (custom servers need `--dangerously-load-development-channels`). It needs claude.ai or Console auth. Team/Enterprise orgs must have it enabled. Docs note: Claude Code does **not** register a channel server that negotiates revision **2026-07-28**, so build it on the 2025-11-25 SDK path. **[OFFICIAL, preview]**
- **Can MCP control another MCP client?** No. Clients do not expose anything; only servers do. To "control" Claude Code you would make Claude Code the server (`claude mcp serve` exposes Claude's *tools*, not its agent loop), or run Claude through `claude -p` or the Agent SDK.
- **MCP as an agent-to-agent transport?** Only by wrapping an agent as a tool ("run_opencode(prompt) → result"). That is RPC with a long-running tool. It is not peer messaging: there is no agent identity, conversation or task lifecycle.
- **Can an MCP server launch a coding agent?** Yes. A server is arbitrary code and can spawn processes. **[DOCUMENTED-API]**
- **Long-running limits in Claude Code** (MCP docs):
  - `MCP_TOOL_TIMEOUT` defaults to about 28 h.
  - Idle timeout is **5 min for HTTP and 30 min for stdio** unless the server sends progress notifications (`CLAUDE_CODE_MCP_TOOL_IDLE_TIMEOUT`).
  - Calls still running after **2 min auto-background** (v2.1.212+, `CLAUDE_CODE_MCP_AUTO_BACKGROUND_MS`). Claude keeps working and gets a task notification later.
  - These tasks **do not survive exiting the session.**
- **MCP Tasks:** experimental in 2025-11-25 (`tasks/get`, `tasks/result`, `notifications/tasks/status`). In 2026-07-28 they moved to an **official extension** `io.modelcontextprotocol/tasks`: poll with `tasks/get`, and `tasks/result` was removed. The C# SDK ships `ModelContextProtocol.Extensions.Tasks`. I found **no Claude Code documentation saying it consumes the tasks extension**, so don't depend on it. Use your own `delegate` / `get_status` / `wait` tools instead.
- **Async completion patterns that work today:**
  - (a) A blocking tool call that Claude Code auto-backgrounds.
  - (b) `delegate_task` returns a `task_id`, then `wait_task(task_id, timeout≤90s)` is a bounded long-poll that Claude repeats.
  - (c) The orchestrator pushes a channel event.
  - (d) An `asyncRewake` hook, which wakes Claude on exit code 2 even when the session is idle.

---

## 3. Agent capability matrix (how an orchestrator drives each agent)

| | Claude Code (as worker or as caller) | OpenCode | Codex CLI |
|---|---|---|---|
| One-shot process | `claude -p "<task>" --output-format json [--json-schema] --permission-mode acceptEdits\|auto --permission-prompts none` | `opencode run --format json --dir <wt> -m provider/model --auto "<task>"` | `codex exec --json -o <file> --sandbox workspace-write [--output-schema f] "<task>"` (stdin accepted) |
| Resume | `--resume <session_id>`, `--continue` | `--session <id>`, `--continue` | `codex exec resume <id>` / `--last` |
| Long-lived server | Agent SDK (TS/Py), `--input-format stream-json` | `opencode serve` (HTTP+SSE, OpenAPI 3.1 at `/doc`, basic auth via `OPENCODE_SERVER_PASSWORD`, default `127.0.0.1:4096`); `opencode run --attach`; `opencode acp` | `codex app-server` (JSON-RPC: `thread/start`, `turn/start`, `turn/completed`, approvals); Codex SDK (TS/Py, wraps CLI with JSONL) |
| Final result | `result` / `structured_output` in JSON; `session_id`; `total_cost_usd` | message `parts`; `/session/:id/diff` | `-o` last message file; JSONL `turn.completed`, `item.completed` |
| Attach to an already-running *interactive* TUI | No (only channels push into one, or Remote Control) | **Yes**: TUI and API share one `serve` backend (`opencode attach <url>`) | App-server clients only; not an arbitrary running TUI |
| Calls *out* to an orchestrator | MCP client, Bash, hooks | MCP client, shell | MCP client, shell |
| .NET SDK | No (use CLI from `Process`) | No (use HTTP/OpenAPI-generated client) | No (use CLI or JSON-RPC) |

Version-specific notes:
- Claude `--bare` (recommended for scripts) **ignores the OAuth login**, so it needs `ANTHROPIC_API_KEY`.
- In `-p`, background Bash shells are killed about 5 s after the result. Background subagents are waited on, up to a 10 min idle ceiling.

---

## 4. Architectures compared

| # | Architecture | Verdict |
|---|---|---|
| 1 | **Pure MCP** (Claude → MCP server that *is* the orchestrator → agents) | Good **front door**. Typed tool schemas, works in Claude, OpenCode and Codex (all are MCP clients). Weak as the **state owner** if the server process lives inside Claude's session: stdio servers die with the session. |
| 2 | **CLI/process** (spawn `opencode run` / `codex exec` / `claude -p`) | **Most reliable worker interface**: exit code = done, stdout JSON = result. Stateless, easy to time out and kill, works on Win/Linux/Docker. Costs MCP/model cold-start per task. |
| 3 | **HTTP/API** (`opencode serve`, Codex app-server, Agent SDK) | Richer: streaming, permission replies, abort, multi-turn. More moving parts. OpenCode had 2026 regressions (`prompt_async` silently dropped in v1.14.44, issue #26635; SSE `/event` closing right after `server.connected`, issue #26697), so pin versions. |
| 4 | OpenCode server | Best "headless agent server" of the three. Fits a "persistent worker per worktree" model. |
| 5 | Codex automation | `codex exec` for v1. App-server for v2 (rich, but a proprietary protocol). The MCP-server mode no longer exists. |
| 6 | Claude Code integration | Caller side: MCP tools, Bash + background notifications, hooks (`asyncRewake`), channels. Worker side: `claude -p` / Agent SDK. |
| 7 | **Agent-to-agent protocols** | **A2A** v1.0 (Linux Foundation, Mar 2026 *(secondary)*): Agent Cards, Task lifecycle (submitted → working → input_required → completed/failed/canceled/rejected). None of the three CLIs speaks A2A natively, so you would build adapters. **ACP** (Agent Client Protocol, Zed/JetBrains) is editor↔agent over stdio JSON-RPC: `opencode acp` built in, `@agentclientprotocol/claude-agent-acp`, `@zed-industries/codex-acp`. ACP gives *one* uniform worker protocol for all three agents. Worth considering as the adapter layer later. |
| 8 | **Custom .NET orchestrator** | Recommended. ASP.NET Core minimal API + `BackgroundService` queue + SQLite + `System.Diagnostics.Process` + official C# MCP SDK (`ModelContextProtocol`, `.AspNetCore`; Apache-2.0). |
| 9 | Existing frameworks | See §7. Use them as references, or claw-orchestrator as a ready-made alternative. |
| 10 | **MCP + custom orchestrator + CLI agents** | **The recommended combination** (§6). |

### Custom "delegate" tool (CLI via Bash) vs MCP: which is more reliable?
Both are thin front doors to the *same* orchestrator service. Reliability comes from **keeping task state in the orchestrator, not in Claude's session**.
- **CLI (`orch delegate … --wait`) via Bash.**
  - Pros: zero protocol surface. Works identically from Claude, OpenCode, Codex and humans. Claude can run it with `run_in_background` and is notified on exit.
  - Cons: background Bash commands get a 30 min / 2 h limit in unattended sessions (none in interactive local sessions). Output is free text unless you print JSON. Needs a Bash permission rule.
- **MCP tools.**
  - Pros: typed schema and discoverability. Auto-backgrounding after 2 min. Progress notifications keep the idle timer alive.
  - Cons: the idle timeout (5 min HTTP / 30 min stdio) if you block without progress. A stdio server process is tied to the session.
- **Verdict:** use an **HTTP-hosted orchestrator** (survives restarts), with an **MCP front door** for Claude and a **CLI front door** for everything else. Use **bounded waits** (`wait_task` returns "still running" after ≤90 s) instead of one multi-hour blocking call. Both front doors are then equally reliable, and MCP gives the model better ergonomics.

---

## 5. Repository / workspace strategy

| Option | Isolation | Problems |
|---|---|---|
| Same working directory | None | Interleaved edits to the same file. An agent works from a stale file view (it read a file before the other agent wrote it). `.git/index.lock` collisions. One agent's `git checkout` / `stash` / `reset` wipes the other's work. Test runs see half-edited code. |
| Branches in one directory | None (only one checkout at a time) | Switching branches under a running agent pulls its files out from under it. |
| **Git worktrees** (`git worktree add -b task/<id> <path> <base>`) | Separate files and index, shared object DB, cheap | A branch can be checked out in only one worktree. Each worktree needs its own `node_modules`/`bin`/`obj`. Port and DB collisions in tests. Git operations that change shared refs should be serialized. Claude Code supports this natively: `claude --worktree`, subagent `isolation: worktree`, `.claude/worktrees/`. |
| Temporary clones | Strongest (separate `.git`) | More disk and time. Push/fetch to integrate. Good for untrusted or containerized workers. |
| Shared Docker volume | Same as "same directory" unless each task gets its own subpath | Use per-task bind mounts of worktrees. Windows bind-mount performance is poor; prefer WSL2-native paths. |

**Recommendation:**
- One worktree and one branch per task, under `<repo>/.orchestrator/worktrees/<task-id>` (gitignored), branched from the caller's current `HEAD`.
- Before delegating, the caller commits or stashes what the subtask depends on (the orchestrator can require a clean `HEAD`, or snapshot with `git stash create`).
- Each worktree has exactly one writer. The orchestrator holds a per-repo mutex around `worktree add/remove`, merges and ref updates.
- After the agent finishes, the orchestrator runs the tests in the worktree, commits any uncommitted changes, and returns `{branch, base_sha, head_sha, diffstat, patch_path, test_summary, agent_summary}`.
- The **caller decides** whether to integrate (`git merge --no-ff task/<id>` or cherry-pick) in its own tree. The orchestrator never edits the caller's working tree. Overlapping edits then show up as normal merge conflicts instead of silent clobbering.
- Garbage-collect with `git worktree remove` + `git worktree prune` and branch deletion after merge or TTL.

---

## 6. Recommended architecture (personal, local-first, Docker-friendly, .NET)

```
                    (A) MCP tools over Streamable HTTP  ─┐
Claude Code ────────(B) Bash: `orch` CLI → HTTP         ─┼──►  Orchestrator (.NET 10, ASP.NET Core)
OpenCode / Codex ───(same A or B, symmetric delegation) ─┘      • REST API  /tasks, /tasks/{id}, /tasks/{id}/wait, SSE /events
                                                                • MCP endpoint (ModelContextProtocol.AspNetCore)
   ◄──(C, optional) push "task done": channel MCP server         • SQLite task store, Channel<T> work queue, BackgroundService
       (--dangerously-load-development-channels) or asyncRewake  • WorktreeManager (git CLI)
       hook polling `orch wait`                                  • Adapters:
                                                                    OpenCodeCliAdapter  → `opencode run --format json`
                                                                    OpenCodeHttpAdapter → `opencode serve` /session API (v2)
                                                                    CodexExecAdapter    → `codex exec --json -o`
                                                                    ClaudeCliAdapter    → `claude -p --output-format json`
                                                                • TestRunner (configurable cmd per repo)
```

### Concrete workflow with real protocols at every arrow

```text
User
 │  terminal / IDE (interactive TUI)
 ▼
Claude Code (host; MCP client)
 │  MCP Streamable HTTP, JSON-RPC `tools/call` → delegate_task{agent:"opencode", prompt, base_ref, test_cmd}
 │  (or: Bash tool runs `orch delegate --agent opencode --wait --json "…"` → HTTP POST /tasks)
 ▼
Orchestrator (.NET service, localhost:7777 or Docker)
 │  1. git CLI: `git worktree add -b task/42 .orchestrator/worktrees/42 <base_sha>`
 │  2. OS process spawn (System.Diagnostics.Process, cwd = worktree, env = auth/permissions):
 │     `opencode run --format json -m <provider/model> --auto "<prompt>"`
 │     — or HTTP to `opencode serve`: POST /session → POST /session/:id/message (blocking) / GET /event (SSE)
 ▼
OpenCode (uses its own stored login: ~/.local/share/opencode/auth.json)
 │  file edits inside the worktree only (OS file I/O)
 ▼
Git worktree `task/42`
 │  OpenCode exits 0, prints JSON events to stdout → orchestrator parses final assistant text
 │  orchestrator: `git add -A && git commit` (if needed), `git diff --stat base..HEAD`
 ▼
Tests (orchestrator runs `dotnet test` / `npm test` as a process in the worktree; exit code + log)
 ▼
Result record in SQLite {status, branch, head_sha, diffstat, patch, tests, summary}
 ▼
Orchestrator
 │  MCP tool result (structuredContent) to the same tools/call — if still running after 2 min Claude Code
 │  auto-backgrounds the call and delivers a task notification on completion;
 │  or wait_task long-poll; or Bash process exit + stdout JSON; (optional) channel notification
 ▼
Claude Code
 │  reads the result, runs `git merge task/42` / reviews diff (its own Bash/Read tools)
 ▼
Continues the original task
```

Codex is the same, with `codex exec --json -o <tmp>/last.txt --sandbox workspace-write "<prompt>"` (cwd = worktree). Its `thread_id` from JSONL is stored so follow-ups use `codex exec resume <id> "<follow-up>"`.

### MCP tool surface (front door A)
- `delegate_task(agent, prompt, base_ref?, model?, test_cmd?, timeout_min?)` → `{task_id}`
- `wait_task(task_id, max_wait_s ≤ 90)` → result or `{status:"running", progress}`
- `get_task(task_id)`
- `list_tasks()`
- `cancel_task(task_id)` (kills the process tree)
- `reply_task(task_id, message)` (resume the session for multi-turn)
- `merge_task(task_id, strategy)` (optional)

### What you build vs reuse
- **Reuse:** the three CLIs; the C# MCP SDK; git; SQLite; Docker.
- **Build (≈ v1 scope):**
  - task model and state machine (queued → running → testing → completed/failed/cancelled, the same states as A2A so an A2A facade can be added later)
  - process supervisor with timeout and kill-tree
  - a JSON event parser per agent
  - the WorktreeManager
  - the MCP + REST + CLI front doors
  - a per-agent concurrency limit
  - log capture
- **Later:** the OpenCode HTTP adapter, the Codex app-server adapter, an ACP-based uniform adapter, a channel push server, an A2A facade, a web dashboard.

### Docker notes
- Run the orchestrator and the agent CLIs in one image (Node for the npm-installed CLIs + the .NET runtime). Mount the repo, and mount credential directories **read-write** (the CLIs write session transcripts and refreshed tokens there; a read-only mount breaks them):
  - `~/.claude` (or pass `CLAUDE_CODE_OAUTH_TOKEN`)
  - `~/.codex`
  - `~/.local/share/opencode`
- Use `codex login --device-auth` for headless login.
- On Windows, run everything inside WSL2 or Docker for path, file-lock and performance reasons. Native Windows works for git worktrees, but watch path lengths.

---

## 7. Existing projects

| Project | What it is | Fit |
|---|---|---|
| **Enderfga/claw-orchestrator** (TS, MIT) | Wraps Claude Code, Codex, OpenCode and others as persistent sessions. Worktree "councils", session handoff. MCP server (`clawo-mcp`), OpenAI-compatible proxy, ACP agent. | Closest existing solution. Try it before building. Not .NET. |
| **openai/codex-plugin-cc** | Official Claude Code plugin: `/codex:rescue`, `/codex:review`, `/codex:status`, `/codex:result`, `/codex:cancel`, `--background`/`--wait`. Uses the local Codex CLI and app-server with your existing Codex login. | **Claude→Codex delegation works officially today.** Good reference design. |
| ACP adapters (`opencode acp`, `@agentclientprotocol/claude-agent-acp`, `@zed-industries/codex-acp`) | Uniform stdio JSON-RPC to each agent | Candidate uniform adapter layer |
| Claude Squad, Vibe Kanban (sunsetting *(secondary)*), Orca, Conductor (macOS), Superset, Nimbalyst | Parallel agents in worktrees for a **human** operator | Worktree UX references; not agent→agent |
| coder/agentapi | HTTP control of agent TUIs via terminal emulation | **Archived 2026-09-13**; avoid |
| Claude Code subagents / agent teams / workflows | Native delegation, Claude-only | Use for Claude→Claude; not cross-vendor |

---

## 8. Authentication: can the orchestrator use each CLI's own logged-in session?

**Yes. The orchestrator just runs the real, unmodified CLI binaries as your OS user, and each one reads its own stored credentials.** No API conversion is needed.

- **Claude Code:**
  - Subscription login (`/login`) is used by `claude -p` *without* `--bare`.
  - For containers or CI: `claude setup-token` creates `CLAUDE_CODE_OAUTH_TOKEN` (valid 1 year, for model requests only).
  - API key: `ANTHROPIC_API_KEY` (required for `--bare`).
  - Policy (legal page): OAuth is for "ordinary use of Claude Code". Products built on the Agent SDK must use API keys, and third parties may not route requests through Free, Pro or Max credentials. The rule "does not prevent an end user from signing in to the unmodified Claude Code binary with their own Claude subscription". Usage limits "assume ordinary, individual usage". → Personally automating your own `claude` CLI with your own login is within the documented mechanisms. Heavy unattended fan-out is where the "ordinary individual usage" assumption gets strained, so prefer an API key for bulk work.
  - **Do not** use a Claude subscription inside OpenCode. OpenCode's docs say Anthropic explicitly prohibits it, and bundled support was removed in 1.3.0.
- **Codex:** ChatGPT login (also `codex login --device-auth` for headless), which `codex exec` reuses. Or API key: `codex login --with-api-key` (piped), or `CODEX_API_KEY` for `exec`. Local models with `--oss` / `--local-provider` (Ollama, LM Studio) and custom `model_providers` in config.
- **OpenCode:** `/connect` or `opencode auth login` stores credentials in `~/.local/share/opencode/auth.json`. Supports ChatGPT Plus/Pro login, GitHub Copilot, GitLab Duo, API keys for most providers (Models.dev catalog), Ollama/LM Studio (`baseURL` e.g. `http://localhost:11434/v1`), and any OpenAI-compatible endpoint via `@ai-sdk/openai-compatible`.
- **Local models / Ollama / OpenAI-compatible:** use them through OpenCode (best support) and Codex `--oss`. Claude Code can target an Anthropic-compatible gateway via `ANTHROPIC_BASE_URL`, but that is outside its supported-provider list.

---

## 8b. When the delegated agent needs a decision mid-task (input-required)

**Short answer: yes, it can come back to the caller and resume after the answer, but only if the orchestrator drives the worker through its *server/SDK interface*, not the one-shot CLI.** One-shot headless modes never pause for a decision. They auto-deny or auto-approve and keep going, or the agent ends its turn with a question in its final text.

There are two kinds of "decision":
- **(P) permission/approval**: "may I run `rm -rf build`?", "may I edit outside the worktree?"
- **(Q) clarifying question**: "Postgres or SQLite?"

What each agent does (verified in source/docs):

| Agent / mode | (P) approvals | (Q) questions | Pauses and waits? | How to answer and resume |
|---|---|---|---|---|
| **OpenCode `run`** (one-shot) | `permission.asked` is **auto-rejected**, or auto-approved with `--auto` (`run.ts`) | The `question` tool is **denied** in non-interactive runs | No | Agent ends its turn. Resume with `opencode run --session <id> "<answer>"` |
| **OpenCode `serve`** (HTTP) | SSE event `permission.asked` → `POST /permission/:requestID/reply` (`once`/`always`/`reject`) (also `POST /session/:id/permissions/:permissionID`) | SSE `question.asked` → `POST /question/:requestID/reply` or `/reject`; `GET /question` lists pending | **Yes**, the session blocks on the pending request | Reply on the endpoint and the same turn continues **[OFFICIAL API]** |
| **Codex `exec`** | Forced `AskForApproval::Never` (`exec/src/lib.rs`): never asks; sandbox decides | No interactive question channel | No | Agent ends its turn. `codex exec resume <id> "<answer>"` |
| **Codex `app-server`** (JSON-RPC) | Server→client requests `item/commandExecution/requestApproval`, `item/fileChange/requestApproval`, `item/permissions/requestApproval` | `item/tool/requestUserInput`; `mcpServer/elicitation/request` | **Yes**, the turn waits for your JSON-RPC response | Send the response and the turn continues **[OFFICIAL protocol]** |
| **Claude Code `-p`** (worker) | `--permission-prompt-tool <mcp tool>`: Claude calls *your* MCP tool, which can block until answered. With `--permission-prompts none` it is denied instead | `AskUserQuestion` is removed when prompts are `none` | **Yes**, via the prompt tool | The prompt tool returns allow/deny and the turn continues |
| **Claude Agent SDK** (worker) | `canUseTool` callback; "can stay pending indefinitely" | `AskUserQuestion` arrives in the same callback; answers go back in `updatedInput.answers` | **Yes** | Return from the callback. For very long waits, a `PreToolUse` hook returns `defer` so the process can exit and **resume later from the persisted session** **[OFFICIAL]** |

### How the orchestrator relays it to Claude Code (the caller)
The orchestrator turns the worker's pending request into a task state, **`input_required`** (the same name A2A and MCP use):

```text
OpenCode serve ──SSE question.asked / permission.asked──► Orchestrator
   (session blocked)                                       task.status = input_required
                                                           task.pending = {request_id, kind, question, options}
Claude Code ──MCP tools/call wait_task(task_id)──► Orchestrator
            ◄── result {status:"input_required", request_id, question, options}
Claude Code decides itself, or asks YOU via its AskUserQuestion tool
Claude Code ──MCP tools/call answer_task(task_id, request_id, answer)──► Orchestrator
Orchestrator ──HTTP POST /question/:requestID/reply (or /permission/:id/reply)──► OpenCode
   (same turn resumes)          task.status = running
Claude Code ──wait_task(task_id)──► … eventually {status:"completed", branch, diff, tests}
```

Codex app-server is the same, except the orchestrator holds the pending JSON-RPC request open and replies to it. A Claude worker is also the same: the orchestrator's MCP prompt-tool / `canUseTool` handler waits on the answer.

### Design rules this adds
1. **Never answer through a blocking call.** `wait_task` must *return* `input_required` instead of blocking. Otherwise Claude Code is stuck inside a tool call while the worker waits on Claude, which is a deadlock until the timeout.
2. **Policy first, escalate second.** The orchestrator auto-answers routine permission requests from a per-task policy: allow edits and tests inside the worktree; deny network, `git push` and paths outside the worktree. Only escalate real questions or out-of-policy actions. This keeps round-trips rare.
3. **Push vs pull.** Pull: Claude polls `wait_task`. If the original MCP call was auto-backgrounded, its task notification fires when the tool returns `input_required`. Push (optional): emit a channel notification or `asyncRewake` hook so an idle Claude session wakes up.
4. **Timeouts.** Each pending request gets a deadline (e.g. 30 min). On expiry: reject with "no answer; choose the safest option and note the assumption", or cancel the task.
5. **Survive restarts.** Persist `session_id` and the pending request. OpenCode/Codex sessions and Claude sessions (`--resume`, SDK `defer`) can all be resumed after the orchestrator restarts. A pending *in-memory* JSON-RPC approval on Codex app-server cannot; it gets rejected and the turn is re-driven with `thread/resume` + `turn/start`.
6. **Fallback for one-shot CLI adapters** (v1): ask workers to finish with structured output (`--json-schema` / `--output-schema`) carrying `{status: "done" | "needs_input", question?, options?}`. The orchestrator maps `needs_input` → `input_required`, and `answer_task` runs the *resume* command (`opencode run --session`, `codex exec resume`, `claude -p --resume`). This works on every agent today. It costs a new turn instead of an in-place continuation, but the conversation context is preserved.

Updated MCP surface: add `answer_task(task_id, request_id, answer | {decision: allow|deny, remember?})`. `get_task` / `wait_task` may return `status: "input_required"` with the pending request.

**Implication for the plan:** v1 can stay CLI-based by using rule 6 (resume-based, universal). Use the **OpenCode HTTP adapter** and **Codex app-server adapter** as soon as you want true in-place pause/resume. That moves them from "later" to "v1.5".

## 8c. Corner cases to design for

Each item is a failure mode followed by its mitigation. The ones that are easiest to miss are marked ★.

**Workspace and Git**
1. ★ **Stale base.** The worktree branches from `HEAD`, but the caller has *uncommitted* edits the subtask depends on. → `delegate_task` snapshots the caller's dirty state (`git stash create` → temp ref `refs/orchestrator/base/<id>`) and branches from that. Record `base_sha` in the result.
2. ★ **Caller keeps editing the same files** while the delegate works. → The caller passes a `scope` (paths) and the orchestrator records an advisory lease ("files X/Y delegated"). Conflicts surface at merge time, never silently.
3. **Gitignored essentials missing in the worktree** (`.env`, `appsettings.Development.json`, local certs). → Keep a per-repo copy list (the same idea as Claude Code's `.worktreeinclude`). Never copy production secrets.
4. **Per-worktree build state.** `node_modules`, `bin/obj`, restore time, disk growth. → A setup command per repo; share package caches (the NuGet global packages folder and the npm cache are safe to share); GC worktrees.
5. ★ **Parallel test collisions.** The same ports, DB names, docker-compose project names, temp dirs. → Inject `PORT_BASE`, `COMPOSE_PROJECT_NAME=task<id>`, a per-task DB name, and `TMPDIR` per task.
6. ★ **A worktree is not a sandbox.** Its `.git` file points at the main repo's shared git dir, so an agent can `git checkout`/`reset --hard`/`push`/delete branches or write via absolute paths outside the worktree. → Deny rules for `git push`, `git reset --hard`, `git checkout <other>`, `git worktree`, and paths outside the worktree, in each agent's permission config (`OPENCODE_PERMISSION`, Claude `--disallowedTools`/settings, Codex sandbox). For strong isolation run the worker in a container that mounts a **clone**, not a worktree.
7. **Branch already checked out / stale worktrees after a crash.** → `git worktree prune` at startup; `git worktree lock` while a task runs; deterministic branch names `task/<id>`.
8. **Submodules and Git LFS** need explicit `git submodule update --init` / `git lfs pull` in new worktrees.
9. **Windows.** CRLF/autocrlf differences inflate diffs. Open file handles block `git worktree remove`. `MAX_PATH`. → Prefer WSL2/Docker; retry removal.

**Agent behavior**
10. ★ **"Success" with no or wrong changes.** The agent claims done but the diff is empty, it touched unrelated files, or it **edited the tests to make them pass**. → Judge by diff + tests, not by the summary. Flag changes to protected paths (`tests/`, CI config, lockfiles) and empty diffs.
11. ★ **Delegation loops and fan-out.** OpenCode delegates back to Claude, which delegates again… → Carry `parent_task_id` and `depth` on each task; set a max depth (e.g. 2), a max concurrent tasks per agent, and a global budget.
12. ★ **Repo-supplied config executes in headless workers.** Claude docs: `claude -p` never shows the trust dialog, and without `--bare` it runs the project's `.claude/settings.json` hooks and `.mcp.json` servers. OpenCode and Codex load `opencode.json` / `AGENTS.md` too. → Only delegate on repos you trust, or run workers with `--bare` (needs an API key) or equivalent restricted config.
13. **Context gap.** The delegate doesn't know what the caller knows. → `delegate_task` requires a self-contained brief: goal, constraints, files of interest, acceptance criteria, test command. Return a structured contract, not prose.
14. **Prompt injection across agents.** Delegate output and repo text can contain instructions. → Treat results as data. Never auto-merge or auto-push.
15. **Permission asymmetry.** A tightly-restricted Claude delegates to a worker running with `--auto`/bypass, which effectively escalates privileges. → The orchestrator enforces one policy for every worker regardless of who called.

**Process and runtime**
16. ★ **Hung or silent workers, and orphan processes** (dev servers or watchers the agent started). → An idle watchdog plus a hard timeout. Kill the **whole process tree**: a Job Object on Windows, a process group/cgroup on Linux, or `docker kill` for containers. Then check that the worktree's ports and files are released.
17. ★ **CLI version drift.** Auto-updates change JSON event shapes (the 2026 OpenCode `prompt_async`/SSE regressions). → Pin versions in the Docker image. Set `OPENCODE_DISABLE_AUTOUPDATE`. Run a startup self-test per adapter. Parse events tolerantly.
18. **stdout hygiene.** Non-JSON lines, huge outputs, UTF-8 on Windows consoles. → Line-delimited parse, size caps, keep raw logs on disk.
19. **Windows process launch.** `opencode`/`codex` are npm `.cmd` shims, so `Process.Start` needs the resolved path or `cmd /c`. The command-line length limit is ~32K chars. → Pass prompts through **stdin or a file** (`codex exec -`, `claude -p` reads stdin), not argv.
20. **First-run and interactive blockers.** Login prompts, trust dialogs (`claude --worktree` interactive needs trust), Codex git-repo checks, OpenCode LSP downloads (`OPENCODE_DISABLE_LSP_DOWNLOAD`). → A pre-flight `doctor` step per adapter (`claude auth status`, `codex login status`, …).

**Auth and limits**
21. ★ **Shared subscription limits.** Five parallel workers on one Claude/ChatGPT plan draw from the same usage window, so you get 429s and mid-task stops. → Per-provider concurrency caps, backoff, a `failed: rate_limited` status that can be resumed later. Claude `-p` supports `--max-budget-usd`.
22. **Credential expiry mid-run** (the Claude login expires; `CLAUDE_CODE_OAUTH_TOKEN` lasts 1 year). Several processes sharing one credential file may race on token refresh (plausible, unverified). → Pre-flight auth check, and prefer the long-lived token or an API key for workers.

**Caller (Claude Code) side**
23. ★ **The caller session ends, restarts or compacts before the result arrives.** Auto-backgrounded MCP calls and background Bash tasks **don't survive exiting the session**, and compaction can drop the `task_id`. → All state lives in the orchestrator. `list_tasks(status=…, parent=…)` lets a fresh session recover. Optionally write `.orchestrator/tasks.md` in the caller's repo.
24. **Duplicate delegation on retry.** Claude re-calls `delegate_task` after a timeout or error. → Idempotency key (hash of brief + base_sha), or a caller-supplied `client_request_id`.
25. **Result too big for context.** Claude Code warns above 10k tokens of MCP output and truncates at 25k by default (`MAX_MCP_OUTPUT_TOKENS`). → Return a summary, diffstat, branch and patch file path. Claude reads details with `git diff` on demand.
26. **Late answers and cancellations.** An `answer_task` arrives after the request timed out, or a cancel arrives while the worker is mid-commit. → Version the pending request (`request_id` must match), make cancel idempotent, and clean up the worktree only after the process exits.
27. **Dependent subtasks and merge order.** Task B needs A's output. → `base_ref` may be another task's branch (a small DAG). Merge in dependency order and re-run tests after each merge.
28. ★ **Parent-session leakage (found while testing the prototype).** If the orchestrator is started from inside an agent session (e.g. from Claude Code's own terminal), child processes inherit variables such as `CLAUDECODE`, `CLAUDE_CODE_SESSION_ID` and the session's IPC socket/token. In testing, a child `claude -p` reported the *parent's* session id. → Strip session-identity and IPC variables from agent child processes (credentials stay). The prototype does this by default (`ScrubEnvironmentVariables`).
29. **Watching replays.** A live view that replays history first must not treat an *old* `input_required`/`completed` event as the end (the task may have resumed since). Check the task's current state before stopping.
30. ★ **Integrating a task that started from uncommitted work (found in testing).** The worktree's base is a snapshot commit that isn't in the caller's history. If the caller later commits the same edits itself, `git merge` of the task branch conflicts on those lines. → Integrate by applying only the agent's delta (`git apply` of base..head), and treat "patch already applied" as integrated when cleaning up.
31. **Byte-exact patches.** Collecting `git diff` output line by line drops the final newline (and on Windows adds CRLF), which gives a "corrupt patch". → Let git write the file (`git diff --binary --output=<file>`).
32. **Windows test commands.** `cmd.exe` has no `grep`/`test`; agents and callers write POSIX commands. → Run test commands in Git Bash when present (never `System32\bash.exe`, which is WSL). Strip ANSI colour codes from captured output.
33. **Agent says "tests pass", independent run says no.** Seen in testing: OpenCode verified with PowerShell and reported success, while the orchestrator's own `grep` run failed. → Make the orchestrator's verdict authoritative and visible.

## 8d. Recommended tech stack

**Pick: .NET 10 (LTS) for the orchestrator. No Node or Python in the orchestrator itself.** Node is needed only inside the worker image, because the agent CLIs are npm packages.

Why .NET is a good fit here (not just the user's preference):
- **Every integration point is a language-neutral protocol**: CLI + JSONL (`claude -p`, `codex exec`, `opencode run`), HTTP + SSE with an OpenAPI 3.1 spec (`opencode serve`, `/doc`), JSON-RPC over stdio (Codex app-server), and MCP. The vendors' TS/Python SDKs are thin wrappers over these same CLIs. The Codex SDK "spawns the CLI and exchanges JSONL", and Claude's docs say to "run the CLI as a subprocess" from other languages. So .NET loses no capability.
- **One thing would normally require Node**, the Agent SDK's `canUseTool` for in-place Claude approvals. It is covered without Node: `claude -p --permission-prompt-tool mcp__orchestrator__approve` points at the orchestrator's **own MCP endpoint**.
- **MCP has an official C# SDK** (`ModelContextProtocol`, `.AspNetCore`, `.Extensions.Tasks`; maintained with Microsoft).
- **Long-running service strengths**:
  - `BackgroundService` and `System.Threading.Channels` for the work queue
  - `Process.Kill(entireProcessTree: true)` plus Windows Job Objects for reliable kill-tree on Windows and Linux
  - strong typing for the task state machine
  - single-file / container publish

| Concern | Choice | Why |
|---|---|---|
| Runtime | .NET 10 LTS, ASP.NET Core minimal APIs | LTS, cross-platform, container-friendly |
| MCP front door | `ModelContextProtocol.AspNetCore` (Streamable HTTP) | Official SDK. HTTP so the server outlives Claude sessions. Add a tiny stdio entry point only if a client needs it |
| Channel push (optional) | Small MCP server on the **2025-11-25** protocol path | Claude Code won't register channel servers on 2026-07-28 |
| REST + events | Minimal APIs + SSE out (`TypedResults.ServerSentEvents`, .NET 10) | Dashboard/CLI/other agents |
| CLI front door | `System.CommandLine`, shipped as a `dotnet tool` (`orch`) | Usable from any agent's Bash tool |
| Task store | SQLite (WAL) via EF Core or Dapper | Zero-ops, file-based, survives restarts |
| Queue / workers | `Channel<T>` + `BackgroundService`, per-agent `SemaphoreSlim` caps | Simple. No broker needed at personal scale |
| Process control | `CliWrap` or raw `Process` + Job Objects / process groups | Streams stdout/stderr, cancellation, kill-tree |
| OpenCode HTTP client | Generate from `/doc` (OpenAPI 3.1) with Kiota or NSwag; `HttpClient` streaming for SSE | Typed client without the JS SDK |
| Codex app-server | Plain `System.Text.Json` newline-delimited JSON-RPC over stdio | Small surface (`thread/*`, `turn/*`, `*requestApproval`). Avoids assumptions about framing that `StreamJsonRpc` would make |
| Git | Shell out to `git` CLI (not LibGit2Sharp) | Worktree, LFS and submodule parity with what the agents themselves run |
| Config / secrets | `appsettings.json` + env vars; credential dirs mounted read-only | Matches each CLI's own auth storage |
| Observability | Serilog or OpenTelemetry; optional .NET Aspire dashboard in dev | Trace task → process → test run |
| Packaging | One Docker image: .NET runtime + Node 22 + **pinned** `@anthropic-ai/claude-code`, `@openai/codex`, `opencode` | Version pinning (corner case 17) |
| UI (later) | None at first. Blazor or a static page reading the SSE feed | Not needed for v1 |

**When another stack would be better:**
- **TypeScript/Node** if you want to *embed* the vendors' SDKs directly (Claude Agent SDK, Codex SDK, OpenCode SDK, ACP libraries are all TS-first), or fork **claw-orchestrator** instead of building. This is the fastest path if .NET isn't a requirement.
- **Python** only if the orchestrator will also host LLM logic of its own (LangGraph etc.). Not needed here.
- **Go/Rust** for a tiny static binary. Weaker MCP/agent SDK ecosystem, and no advantage at personal scale.

## 8e. Seeing what a delegated agent is doing

A headless child process has no window, so visibility has to be built in:

| Mechanism | Live? | Agents |
|---|---|---|
| Normalized event stream (messages, commands, file edits, errors, test output) from each CLI's JSON output → dashboard / `orch watch` / MCP `get_task_events` | Yes | All |
| OpenCode tasks run inside a shared `opencode serve` (`opencode run --attach <url>`), and the human runs `opencode attach <url> --session <id>` to open the real TUI on the same session | Yes, native UI | OpenCode **[OFFICIAL CLI]** |
| `claude --resume <id>` / `codex resume <id>` in the task's worktree | After the turn, native UI | Claude Code, Codex |
| Open the worktree folder in an editor (files change on disk) | Yes | All |

Neither Claude Code nor Codex documents attaching a UI to a running headless (`-p` / `exec`) run, so for those the event stream is the live view.

## 9. Decision guidance

- **Want Claude→Codex only, now?** Install `openai/codex-plugin-cc`. Done, officially.
- **Want Claude↔OpenCode↔Codex with your own control, in .NET?** Build the orchestrator in §6. Start with the CLI adapters plus the MCP and CLI front doors; that is enough for the full workflow. Add the OpenCode HTTP and Codex app-server adapters only when you need multi-turn steering or streaming.
- **Want something running today without coding?** Evaluate claw-orchestrator first, and treat it as a reference even if you build your own.
- **Don't** build on: `codex mcp-server` (removed), `coder/agentapi` (archived), MCP sampling (deprecated), or MCP tasks as the completion signal into Claude Code (not documented as consumed).

## 10. Key sources

- Claude Code: https://code.claude.com/docs/en/headless · /mcp · /channels · /channels-reference · /hooks · /tools-reference · /worktrees · /authentication · /legal-and-compliance · /agent-sdk/overview
- MCP spec: https://github.com/modelcontextprotocol/modelcontextprotocol/tree/main/docs/specification (2025-11-25 `basic/utilities/tasks`, 2026-07-28 `changelog`, `architecture`, `transports/stdio`); C# SDK https://github.com/modelcontextprotocol/csharp-sdk
- OpenCode: https://github.com/anomalyco/opencode docs `server.mdx`, `cli.mdx`, `providers.mdx`, `acp.mdx`; issues #26635, #26697
- Codex: https://github.com/openai/codex (`codex-rs/exec/src/cli.rs`, `exec_events.rs`, `app-server/README.md`, `sdk/typescript`), release rust-v0.154.0, PR #42993; https://github.com/openai/codex-plugin-cc
- Others: https://github.com/Enderfga/claw-orchestrator · https://github.com/coder/agentapi · A2A https://en.wikipedia.org/wiki/Agent2Agent · ACP https://zed.dev/acp
