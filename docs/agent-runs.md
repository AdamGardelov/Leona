# Agent runs and approvals

Runs belong to the signed-in profile. Ollama inference runs in a background worker; SSE only delivers persisted events. Shell commands are available only through the approval-gated `run_command`; Playwright is not exposed.

## Lifecycle

- `POST /api/runs` accepts `{ conversationId, input: { text, model, think, web, files, commands, accounts, attachments? }, rewindFromMessageId? }`. Attachments are upload IDs from `POST /api/uploads` (multipart); their names and types are always taken from the upload store, and text may be empty when files are attached and returns 202 with run ID, starting message ID and status. `rewindFromMessageId` must name a user message in that conversation; it and every later message (with their saved tool evidence) are deleted before the run starts, which is how editing and regenerating work. Starting a run restores an archived conversation. One active run per conversation; different conversations can run concurrently. A generation gate serializes model inference and is released before tools or approval waits.
- `GET /api/runs/active` discovers active runs. `GET /api/runs/{id}` returns the run snapshot. States are `queued`, `running`, `awaiting_approval`, `completed`, `cancelled`, `failed`, `interrupted`.
- `GET /api/runs/{id}/events` replays ordered persisted SSE events, with `id` and support for `Last-Event-ID`. Disconnecting only detaches the viewer. The UI reconstructs output from starting history plus the event log and reconnects using EventSource. Reload discovers active runs or the remembered run.
- `GET /api/runs/{id}/log` returns the run, its ordered events and the action ledger for the inspector. `GET /api/conversations/{id}/runs` lists a conversation's latest runs.
- `POST /api/runs/{id}/cancel` cancels execution and invalidates unused approvals. Partial assistant output is saved before the terminal event. Completed runs and events remain in SQLite until their conversation is deleted. Active conversations cannot be deleted.

## Events

Besides `content`, `thinking`, `status`, `error` and `run_finished`, a run emits:

- `model_request` per inference round, with the context window, output reserve, estimated tokens, offered tools and each message's role, size and preview;
- `context` (estimate) and `usage` (measured `promptTokens`, `evalTokens`, `evalDurationMs`, `doneReason`, `round`);
- `tool_started` (`id`, `name`, `arguments`) and `tool_finished` (`id`, `status`: completed, failed, rejected, expired or unavailable, a short `text` summary, `sources`, `durationMs` and a result excerpt in `detail`);
- `truncated` when the final round stopped at the output limit, and `title` when the conversation was named.

The tool call ID is also the RunAction and approval ID, so approvals attach to their step.

## Approvals and execution ledger

`create_file`, `edit_file`, `run_command` and the personal tools that act (sending mail, calendar changes, home actions and the like) require approval; reading does not. After a run has read untrusted content, opening an unknown page and saving a memory need approval too. The registry declares the policy. Every tool invocation records exact arguments and status in RunActions. Before asking, the registry checks the call (`ProposeAsync`): impossible calls are recorded as `failed` and returned to the model without an approval. Otherwise the action is persisted as pending and `approval_required` carries the approval ID, tool name, arguments, a `preview` (a diff for edits; folder, command and timeout for commands) and the expiration. For `edit_file` the proposal also fingerprints the file (SHA-256); execution refuses if the file changed after the user saw the diff.

`POST /api/runs/{id}/approvals/{approvalId}` accepts only `{ approve: true|false, always?: bool }`; `always` also trusts a read_page site for the profile. It checks the run, action, pending state, expiration and cancellation, persists the decision, and wakes the worker. It never accepts replacement arguments. Duplicate or stale decisions return 409. Approved actions transition once to executing before the tool is invoked; completion and a result excerpt are persisted. Rejection becomes a tool result so the model can continue. Proposals expire after ten minutes.

On backend startup, unfinished runs and actions become interrupted. They are never replayed automatically. If a crash happens during a write, the outcome may be unknown: inspect the workspace before asking to retry. The ledger preserves the proposed action and execution status; atomic exactly-once external effects are not claimed.

## Scope and validation

This app remains localhost-only, without login or authenticated ownership. Host checks and cross-origin write checks protect the local browser workflow; they are not authentication. Add authentication/authorization and run ownership before remote or phone access. Current allowed browser origins are HTTP localhost/127.0.0.1/IPv6 loopback on ports 5173 and 5080.

Tests: `dotnet run --project tests/Harness.Checks` and, after building the backend, `python3 tests/run_lifecycle.py`. The latter runs an isolated server with a fake Ollama, temporary SQLite and workspace. It checks approval/decline, duplicate decisions, SSE cursor replay, disconnect persistence, simultaneous conversations, cancellation, restart recovery, deletion, cross-origin protection, editing by rewind, titles, the run log, settings, pins and the archive. Production model behavior may vary; the approval policy is enforced by the backend regardless of model behavior.
