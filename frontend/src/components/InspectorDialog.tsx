import { useEffect, useState } from 'react';
import { api, type RunAction, type RunEvent, type RunLog, type StepStatus } from '../api';
import { describeStep, statusLabels } from '../steps';
import { Dialog } from './Dialog';

type RequestDetail = {
  contextWindow?: number;
  numPredict?: number;
  estimatedTokens?: number;
  tools?: string[];
  messages?: { role: string; chars: number; toolCalls?: string[] | null; preview: string }[];
};

type ToolEntry = { started: RunEvent; finished?: RunEvent; action?: RunAction };
type Round = { request?: RunEvent; usage?: RunEvent; tools: ToolEntry[]; notes: RunEvent[] };

// Groups the flat event log into model rounds, each with its request, usage and tool calls.
function group(log: RunLog) {
  const rounds: Round[] = [];
  const preamble: RunEvent[] = [];
  const tools = new Map<string, ToolEntry>();
  let current: Round | undefined;
  for (const event of log.events) {
    if (event.type === 'model_request') {
      current = { request: event, tools: [], notes: [] };
      rounds.push(current);
    } else if (event.type === 'usage' && current) {
      current.usage = event;
    } else if (event.type === 'tool_started' && current && event.id) {
      const entry: ToolEntry = {
        started: event,
        action: log.actions.find((a) => a.id === event.id),
      };
      tools.set(event.id, entry);
      current.tools.push(entry);
    } else if (event.type === 'tool_finished' && event.id && tools.has(event.id)) {
      tools.get(event.id)!.finished = event;
    } else if (!['content', 'thinking', 'context', 'usage', 'done'].includes(event.type)) {
      (current ? current.notes : preamble).push(event);
    }
  }
  return { rounds, preamble };
}

function Note({ event }: { event: RunEvent }) {
  const text =
    event.type === 'approval_required'
      ? 'Approval requested'
      : event.type === 'approval_resolved'
        ? event.approved
          ? 'Approved'
          : 'Declined or expired'
        : event.type === 'run_finished'
          ? `Run ${event.status}${event.text ? `: ${event.text}` : ''}`
          : event.type === 'title'
            ? `Named “${event.text}”`
            : (event.text ?? event.type);
  return <li className={`inspect-note ${event.type}`}>{text}</li>;
}

export function InspectorDialog({
  runId,
  open,
  onClose,
}: {
  runId: string | null;
  open: boolean;
  onClose: () => void;
}) {
  const [log, setLog] = useState<RunLog | null>(null);
  const [error, setError] = useState('');

  function load() {
    if (!runId) {
      return;
    }
    setError('');
    api<RunLog>(`/runs/${runId}/log`)
      .then(setLog)
      .catch((e) => setError(String(e)));
  }

  useEffect(() => {
    if (open) {
      setLog(null);
      load();
    }
  }, [open, runId]);

  const grouped = log ? group(log) : null;
  const totals = grouped?.rounds.reduce(
    (sum, round) => ({
      output: sum.output + (round.usage?.evalTokens ?? 0),
      ms: sum.ms + (round.usage?.evalDurationMs ?? 0),
    }),
    { output: 0, ms: 0 },
  );

  return (
    <Dialog title="Inspect run" open={open} wide onClose={onClose}>
      {!log || !grouped ? (
        <p className="dialog-loading">{error || 'Loading…'}</p>
      ) : (
        <div className="inspector">
          <dl className="inspect-summary">
            <div>
              <dt>Status</dt>
              <dd>{log.run.status}</dd>
            </div>
            <div>
              <dt>Model</dt>
              <dd className="mono">{log.run.input.model}</dd>
            </div>
            <div>
              <dt>Options</dt>
              <dd>
                {[
                  log.run.input.web && 'Web',
                  log.run.input.files && 'Files',
                  log.run.input.think && 'Thinking',
                ]
                  .filter(Boolean)
                  .join(', ') || 'Chat only'}
              </dd>
            </div>
            <div>
              <dt>Rounds</dt>
              <dd>{grouped.rounds.length}</dd>
            </div>
            <div>
              <dt>Output</dt>
              <dd>
                {totals?.output.toLocaleString()} tokens
                {totals &&
                  totals.ms > 0 &&
                  ` · ${(totals.output / (totals.ms / 1000)).toFixed(1)} tok/s`}
              </dd>
            </div>
            <button className="secondary" onClick={load}>
              Refresh
            </button>
          </dl>
          {grouped.preamble.length > 0 && (
            <ul className="inspect-notes">
              {grouped.preamble.map((event, i) => (
                <Note key={i} event={event} />
              ))}
            </ul>
          )}
          {grouped.rounds.map((round, index) => {
            const detail = (round.request?.detail ?? {}) as RequestDetail;
            return (
              <section className="inspect-round" key={index}>
                <h3>Round {index + 1}</h3>
                <p className="inspect-meta">
                  Sent ≈{detail.estimatedTokens?.toLocaleString()} tokens to a{' '}
                  {detail.contextWindow?.toLocaleString()}-token window, room for{' '}
                  {detail.numPredict?.toLocaleString()} output tokens.{' '}
                  {detail.tools?.length
                    ? `Tools offered: ${detail.tools.join(', ')}.`
                    : 'No tools offered.'}
                </p>
                {round.usage && (
                  <p className="inspect-meta">
                    Measured {round.usage.promptTokens?.toLocaleString() ?? '?'} prompt tokens ·{' '}
                    {round.usage.evalTokens?.toLocaleString() ?? '?'} output tokens
                    {round.usage.evalTokens && round.usage.evalDurationMs
                      ? ` · ${(round.usage.evalTokens / (round.usage.evalDurationMs / 1000)).toFixed(1)} tok/s`
                      : ''}{' '}
                    · stopped: <strong>{round.usage.doneReason ?? 'unknown'}</strong>
                  </p>
                )}
                <details>
                  <summary>Messages sent ({detail.messages?.length ?? 0})</summary>
                  <table className="inspect-table">
                    <thead>
                      <tr>
                        <th scope="col">Role</th>
                        <th scope="col">Characters</th>
                        <th scope="col">Preview</th>
                      </tr>
                    </thead>
                    <tbody>
                      {detail.messages?.map((m, i) => (
                        <tr key={i}>
                          <td>
                            {m.role}
                            {m.toolCalls?.length ? ` → ${m.toolCalls.join(', ')}` : ''}
                          </td>
                          <td className="mono">{m.chars.toLocaleString()}</td>
                          <td>
                            <span className="preview">{m.preview || '—'}</span>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </details>
                {round.tools.map((entry) => {
                  const status = (entry.finished?.status ?? 'running') as StepStatus;
                  const label = describeStep(
                    entry.started.name ?? '',
                    entry.started.arguments ?? {},
                    status,
                  );
                  const result = entry.finished?.detail?.result;
                  return (
                    <div className={`inspect-tool ${status}`} key={entry.started.id}>
                      <div className="inspect-tool-head">
                        <strong className="mono">{entry.started.name}</strong>
                        <span>{label.detail}</span>
                        <span className="inspect-status">
                          {statusLabels[status] ?? status}
                          {entry.finished?.durationMs !== undefined &&
                            ` · ${(entry.finished.durationMs / 1000).toFixed(1)} s`}
                          {entry.action && ` · ledger: ${entry.action.status}`}
                        </span>
                      </div>
                      <details>
                        <summary>Arguments and result</summary>
                        <pre>{JSON.stringify(entry.started.arguments ?? {}, null, 2)}</pre>
                        {typeof result === 'string' && <pre>{result}</pre>}
                      </details>
                    </div>
                  );
                })}
                {round.notes.length > 0 && (
                  <ul className="inspect-notes">
                    {round.notes.map((event, i) => (
                      <Note key={i} event={event} />
                    ))}
                  </ul>
                )}
              </section>
            );
          })}
        </div>
      )}
    </Dialog>
  );
}
