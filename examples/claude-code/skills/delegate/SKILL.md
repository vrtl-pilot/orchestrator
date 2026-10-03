---
name: delegate
description: Delegate a well-scoped coding subtask to another coding agent (Codex, OpenCode, or another Claude Code) through the Agent Orchestrator MCP server, wait for it, answer its questions, and merge its branch. Use when the user asks to hand work to Codex/OpenCode/another agent, or to parallelize independent subtasks.
---

# Delegating work through the Agent Orchestrator

The `orchestrator` MCP server runs other coding agents in **isolated git worktrees**. Your own working tree
is never modified; each delegated task produces a branch you merge yourself.

## 1. Decide whether to delegate

Delegate only work that is **self-contained** (clear inputs, clear done-criteria, mostly separate files).
Don't delegate tiny edits, or work that needs your conversation context you can't write down. Don't
delegate further if you are yourself a delegated agent (`ORCHESTRATOR_TASK_ID` is set) unless asked.

## 2. Start the task

Call `delegate_task` with:
- `agent`: `codex`, `opencode` or `claude` (call `list_agents` if unsure what is installed).
- `repo_path`: your current working directory (absolute).
- `prompt`: a **self-contained brief**: goal, constraints, the files involved, acceptance criteria, and
  anything you already learned. The agent cannot see this conversation.
- `test_command` when there is one (e.g. `dotnet test`, `npm test`). The orchestrator runs it afterwards, in Git Bash on
  Windows when available (otherwise cmd.exe), so keep it portable.

Your uncommitted changes are included in the task's starting point by default. Avoid editing the same
files while the task runs; you will merge its branch later.

## 3. Wait without blocking forever

Call `wait_task` (it returns within ~60–90 s). While the status is `queued`/`running`/`testing`, either
call it again or do other useful work and come back. Use `get_task_events` to see what the agent is
doing. Tell the user the `dashboardUrl` if they want to watch live.

## 4. Handle questions (`input_required`)

If `wait_task` returns `status: input_required`, the agent stopped to ask `pendingQuestion`.
- If the answer follows from the user's request or the codebase, decide yourself.
- If it is the user's call, ask the user, then pass their answer on.
Send it with `answer_task(task_id, answer)` and go back to step 3. The agent resumes in the same session.

## 5. Review and integrate (`completed`)

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

## 6. Failures

`failed` tasks keep any partial work on their branch. Read `error`, then either `continue_task` with
corrections, re-delegate with a better brief, or do the work yourself. Use `cancel_task` to stop a task
that is going in the wrong direction.

After a context reset, use `list_tasks` to find task ids again; all state lives in the orchestrator.
