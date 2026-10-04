import { useEffect, useRef, useState, type Dispatch, type SetStateAction } from 'react';
import {
  api,
  send,
  type AgentRun,
  type Approval,
  type Message,
  type RunEvent,
  type StepStatus,
  type ToolStep,
  type MailItem,
} from './api';
import { describeStep } from './steps';

export type TimelineItem = {
  key: string;
  text: string;
  state: 'info' | 'running' | 'waiting' | 'done' | 'warning' | 'failed';
};
export type Usage = {
  prompt?: number;
  estimate?: number;
  window: number;
  outputTokens?: number;
  tokensPerSecond?: number;
};

type Options = {
  setMessages: Dispatch<SetStateAction<Message[]>>;
  setError: (message: string) => void;
  // Called when a run is attached, so the app can show its conversation and input settings.
  onStart: (run: AgentRun) => void;
  onFinished: (run: AgentRun, saved: Message[] | null) => void;
  onTitle: () => void;
};

const activeRunKey = 'leona-active-run';

function rememberRun(id: string | null) {
  try {
    if (id) {
      localStorage.setItem(activeRunKey, id);
    } else {
      localStorage.removeItem(activeRunKey);
    }
  } catch {
    // Storage is optional; reload then finds the run through /runs/active.
  }
}

export function rememberedRun() {
  try {
    return localStorage.getItem(activeRunKey);
  } catch {
    return null;
  }
}

function stepState(status: string): TimelineItem['state'] {
  if (status === 'completed') {
    return 'done';
  }
  return status === 'failed' ? 'failed' : 'warning';
}

// Follows one persisted run over SSE and turns its events into reply text, tool steps and a timeline.
export function useRun(options: Options) {
  const [busy, setBusy] = useState(true);
  const [runId, setRunId] = useState<string | null>(null);
  const [lastRunId, setLastRunId] = useState<string | null>(null);
  const [approval, setApproval] = useState<Approval | null>(null);
  const [deciding, setDeciding] = useState(false);
  const [usage, setUsage] = useState<Usage | null>(null);
  const [timeline, setTimeline] = useState<TimelineItem[]>([]);
  const source = useRef<EventSource | null>(null);
  const version = useRef(0);
  const latest = useRef(options);
  latest.current = options;

  useEffect(() => {
    return () => {
      version.current++;
      source.current?.close();
    };
  }, []);

  function reset() {
    setUsage(null);
    setTimeline([]);
    setApproval(null);
  }

  function updateReply(change: (message: Message) => Message) {
    latest.current.setMessages((previous) =>
      previous.map((m, index) => (index === previous.length - 1 ? change(m) : m)),
    );
  }

  function updateStep(id: string | undefined, change: Partial<ToolStep>) {
    updateReply((m) => ({
      ...m,
      tools: (m.tools ?? []).map((step) => (step.id === id ? { ...step, ...change } : step)),
    }));
  }

  function upsertTimeline(item: TimelineItem) {
    setTimeline((previous) =>
      previous.some((i) => i.key === item.key)
        ? previous.map((i) => (i.key === item.key ? item : i))
        : [...previous, item],
    );
  }

  function handle(event: RunEvent, key: string, run: AgentRun) {
    switch (event.type) {
      case 'content':
        updateReply((m) => ({ ...m, content: m.content + (event.text ?? '') }));
        break;
      case 'thinking':
        updateReply((m) => ({ ...m, thinking: (m.thinking ?? '') + (event.text ?? '') }));
        break;
      case 'context':
        setUsage((previous) => ({
          ...previous,
          estimate: event.estimatedTokens,
          prompt: undefined,
          window: event.contextWindow ?? previous?.window ?? 0,
        }));
        break;
      case 'usage':
        setUsage((previous) => ({
          ...previous,
          prompt: event.promptTokens,
          window: event.contextWindow ?? previous?.window ?? 0,
          outputTokens: (previous?.outputTokens ?? 0) + (event.evalTokens ?? 0),
          tokensPerSecond:
            event.evalTokens && event.evalDurationMs
              ? event.evalTokens / (event.evalDurationMs / 1000)
              : previous?.tokensPerSecond,
        }));
        break;
      case 'status':
        upsertTimeline({ key, text: event.text ?? '', state: 'info' });
        break;
      case 'tool_started': {
        const step: ToolStep = {
          id: event.id,
          name: event.name ?? '',
          arguments: event.arguments ?? {},
          status: 'running',
        };
        updateReply((m) => ({ ...m, tools: [...(m.tools ?? []), step] }));
        const label = describeStep(step.name, step.arguments, 'running');
        upsertTimeline({
          key: `tool-${event.id}`,
          text: [label.title, label.detail].filter(Boolean).join(' · '),
          state: 'running',
        });
        break;
      }
      case 'approval_required':
        setApproval({
          approvalId: event.approvalId ?? '',
          toolName: event.toolName ?? '',
          arguments: event.arguments ?? {},
          preview: event.preview,
          expiresAt: event.expiresAt ?? '',
        });
        updateStep(event.approvalId, { status: 'awaiting_approval' });
        setTimeline((previous) =>
          previous.map((i) =>
            i.key === `tool-${event.approvalId}` ? { ...i, state: 'waiting' } : i,
          ),
        );
        break;
      case 'approval_resolved':
        setApproval(null);
        updateStep(event.approvalId, { status: 'running' });
        setTimeline((previous) =>
          previous.map((i) =>
            i.key === `tool-${event.approvalId}` ? { ...i, state: 'running' } : i,
          ),
        );
        break;
      case 'tool_finished': {
        const status = (event.status ?? 'completed') as StepStatus;
        const drafts = event.detail?.drafts;
        const mails = event.detail?.mails;
        updateStep(event.id, {
          status,
          summary: event.text,
          sources: event.sources,
          durationMs: event.durationMs,
          drafts: Array.isArray(drafts) ? drafts.map(String) : undefined,
          mails: Array.isArray(mails) && mails.length > 0 ? (mails as MailItem[]) : undefined,
        });
        setTimeline((previous) =>
          previous.map((i) =>
            i.key === `tool-${event.id}`
              ? {
                  ...i,
                  text: event.text ? `${i.text.split(' · ')[0]} · ${event.text}` : i.text,
                  state: stepState(status),
                }
              : i,
          ),
        );
        break;
      }
      case 'truncated':
        updateReply((m) => ({ ...m, truncated: true }));
        upsertTimeline({ key, text: event.text ?? 'Answer truncated', state: 'warning' });
        break;
      case 'error':
        latest.current.setError(event.text ?? 'The run failed.');
        upsertTimeline({ key, text: event.text ?? 'Error', state: 'failed' });
        break;
      case 'title':
        upsertTimeline({ key, text: `Named “${event.text}”`, state: 'info' });
        latest.current.onTitle();
        break;
      case 'run_finished':
        finish(run, event);
        upsertTimeline({
          key,
          text: `Run ${event.status}`,
          state: event.status === 'completed' ? 'done' : 'warning',
        });
        break;
    }
  }

  function finish(run: AgentRun, event: RunEvent) {
    const current = version.current;
    source.current?.close();
    setApproval(null);
    setRunId(null);
    if (event.status === 'interrupted' || event.status === 'failed') {
      latest.current.setError(
        event.text ?? `Run ${event.status}. Unfinished actions were not replayed.`,
      );
    }
    if (rememberedRun() === run.id) {
      rememberRun(null);
    }
    void api<Message[]>(`/conversations/${run.conversationId}/messages`)
      .then((saved) => {
        if (current === version.current) {
          latest.current.onFinished(run, saved);
        }
      })
      .catch((e) => {
        latest.current.setError(String(e));
        latest.current.onFinished(run, null);
      })
      .finally(() => {
        if (current === version.current) {
          setBusy(false);
        }
      });
  }

  async function watch(run: AgentRun) {
    const current = ++version.current;
    source.current?.close();
    setBusy(true);
    setRunId(run.id);
    setLastRunId(run.id);
    reset();
    rememberRun(run.id);
    latest.current.onStart(run);
    const history = await api<Message[]>(`/conversations/${run.conversationId}/messages`);
    if (current !== version.current) {
      return;
    }
    // Reconstruct from the run's starting history and its event log, avoiding duplicated text on reload.
    latest.current.setMessages([
      ...history.filter((m) => (m.id ?? 0) <= run.baseMessageId),
      { role: 'user', content: run.input.text, attachments: run.input.attachments ?? [] },
      { role: 'assistant', content: '', tools: [], model: run.input.model },
    ]);
    const events = new EventSource(`/api/runs/${run.id}/events`);
    source.current = events;
    let lastEvent = 0;
    events.onopen = () => {
      if (current === version.current) {
        latest.current.setError('');
      }
    };
    events.onerror = () => {
      if (current === version.current && events.readyState !== EventSource.CLOSED) {
        latest.current.setError(
          'Connection lost. Reconnecting; the run continues on your computer.',
        );
      }
    };
    events.onmessage = (message) => {
      if (current !== version.current || Number(message.lastEventId) <= lastEvent) {
        return;
      }
      lastEvent = Number(message.lastEventId);
      handle(JSON.parse(message.data) as RunEvent, `event-${lastEvent}`, run);
    };
  }

  async function stop() {
    if (!runId) {
      return;
    }
    try {
      await send(`/runs/${runId}/cancel`, 'POST');
      upsertTimeline({ key: 'stopping', text: 'Stopping…', state: 'info' });
    } catch (e) {
      latest.current.setError(e instanceof Error ? e.message : String(e));
    }
  }

  async function decide(approve: boolean, always = false) {
    if (!runId || !approval || deciding) {
      return;
    }
    setDeciding(true);
    try {
      await send(`/runs/${runId}/approvals/${approval.approvalId}`, 'POST', { approve, always });
      setApproval(null);
      latest.current.setError('');
    } catch (e) {
      latest.current.setError(e instanceof Error ? e.message : String(e));
    } finally {
      setDeciding(false);
    }
  }

  // Leaves the current run without cancelling it, for example after the conversation is closed.
  function detach() {
    version.current++;
    source.current?.close();
  }

  return {
    busy,
    setBusy,
    runId,
    lastRunId,
    setLastRunId,
    approval,
    deciding,
    usage,
    timeline,
    watch,
    stop,
    decide,
    reset,
    detach,
  };
}
