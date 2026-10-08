---
name: agent-orchestrator
description: Delegate a well-scoped coding subtask to another coding agent platform (Claude Code, Codex, OpenCode, Qoder or a custom one) through the Agent Orchestrator MCP server, wait for it, answer its questions, and integrate its result. Use when the user asks to hand work to another agent or platform, or to parallelize independent subtasks.
---

<!-- Installed by Agent Orchestrator. Re-running `orch setup` or the Setup page updates this file. -->

# Delegating work through the Agent Orchestrator

The `orchestrator` MCP server runs other coding agents in **isolated git worktrees**. Your own working tree
is never modified; each delegated task produces a branch you integrate yourself.
Dashboard (live view of every task): http://127.0.0.1:7777/

## 1. Decide whether to delegate

Delegate only work that is **self-contained** (clear inputs, clear done-criteria, mostly separate files).
Don't delegate tiny edits, or work that needs your conversation context you can't write down. Don't
delegate further if you are yourself a delegated agent (`ORCHESTRATOR_TASK_ID` is set) unless asked.

## 2. Let the user choose the platform

Unless the user already named the platform, **ask them; never pick one yourself**:
1. Call `list_agents`. It returns the `available` platforms (with their default model) and the `unavailable` ones
   (with how to install them).
2. Ask the user which available platform should do the task, using your ask-the-user tool if you have one. List each
   option with its model, and mention notable unavailable ones only if relevant.
3. Use their answer as `agent`. If you call `delegate_task` without `agent`, it starts nothing and returns the same
   choices, so you can always ask then.

## 3. Start the task

Call `delegate_task` with:
- `agent`: the platform the user chose.
- `repo_path`: your current working directory (absolute).
- `prompt`: a **self-contained brief**: goal, constraints, the files involved, acceptance criteria, and
  anything you already learned. The agent cannot see this conversation.
- `test_command` when there is one (e.g. `dotnet test`, `npm test`). The orchestrator runs it afterwards, in Git Bash on
  Windows when available (otherwise cmd.exe), so keep it portable.

Your uncommitted changes are included in the task's starting point by default. Avoid editing the same
files while the task runs; you will merge its branch later.

## 4. Wait without blocking forever

Call `wait_task` (it returns within ~60–90 s). While the status is `queued`/`running`/`testing`, either
call it again or do other useful work and come back. Use `get_task_events` to see what the agent is
doing. Tell the user the `dashboardUrl` if they want to watch live.

## 5. Handle questions (`input_required`)

If `wait_task` returns `status: input_required`, the agent stopped to ask `pendingQuestion`.
- If the answer follows from the user's request or the codebase, decide yourself.
- If it is the user's call, ask the user, then pass their answer on.
Send it with `answer_task(task_id, answer)` and go back to step 4. The agent resumes in the same session.

## 6. Review and integrate (`completed`)

1. Read `summary`, `diffStat`, `changedFiles`, `testsPassed` / `testOutputTail`. If `needsAttention` is true or
   `testsPassed` is false, do not merge: the orchestrator's own test run outranks the agent's claim that tests pass.
2. Inspect the diff: `git diff <baseSha> <branch>` (or read the `patchPath` file).
3. If it needs changes, use `continue_task(task_id, message)` and wait again.
4. If good, integrate it, following `nextStep`:
   - `baseIncludesUncommitted: true` (the task started from your uncommitted edits): do **not** `git merge`.
     That snapshot commit isn't in your history, so a merge conflicts with your own copy of those edits. Apply only
     the agent's changes: `git apply "<patchPath>"`. If that fails because you changed the files since, commit your
     work and use `git apply --3way "<patchPath>"`.
   - Otherwise: `git merge --no-ff <branch>` (or the same `git apply`).
5. Call `cleanup_task(task_id, delete_branch=true)` **after** merging. It refuses to delete an unmerged branch
   (see `notice`). Use `force=true` only when the user agrees to discard the work.

## 7. Failures

`failed` tasks keep any partial work on their branch. Read `error`, then either `continue_task` with
corrections, re-delegate with a better brief, or do the work yourself. Use `cancel_task` to stop a task
that is going in the wrong direction.

After a context reset, use `list_tasks` to find task ids again; all state lives in the orchestrator.
