import { useEffect, useState } from 'react';
import { api, send, type ScheduledTask, type Settings, type Watch } from '../api';
import { Icon } from '../icons';
import { Dialog } from './Dialog';

const dayNames = ['M', 'T', 'W', 'T', 'F', 'S', 'S'];
const dayLabels = ['Monday', 'Tuesday', 'Wednesday', 'Thursday', 'Friday', 'Saturday', 'Sunday'];
const intervals = [15, 30, 60, 180, 720, 1440];

type TaskDraft = {
  id?: number;
  name: string;
  prompt: string;
  time: string;
  days: number;
  // Empty follows the default model from Settings.
  model: string;
  web: boolean;
  files: boolean;
  accounts: boolean;
  enabled: boolean;
};

const emptyTask: TaskDraft = {
  name: '',
  prompt: '',
  time: '07:00',
  days: 31,
  model: '',
  web: true,
  files: false,
  accounts: true,
  enabled: true,
};

function when(value?: string | null) {
  return value
    ? new Date(value).toLocaleString([], {
        weekday: 'short',
        hour: '2-digit',
        minute: '2-digit',
      })
    : '—';
}

function Switch({
  checked,
  label,
  onChange,
}: {
  checked: boolean;
  label: string;
  onChange: (checked: boolean) => void;
}) {
  return (
    <button
      type="button"
      role="switch"
      aria-checked={checked}
      aria-label={label}
      className="inline-switch"
      onClick={() => onChange(!checked)}
    >
      <span className="switch" aria-hidden="true" />
    </button>
  );
}

// Scheduled prompts (like the morning brief) and page watches, each with a notification when there is news.
export function AutomationsDialog({
  open,
  onClose,
  onOpenConversation,
}: {
  open: boolean;
  onClose: () => void;
  onOpenConversation: (id: number) => void;
}) {
  const [tab, setTab] = useState<'tasks' | 'watches'>('tasks');
  const [tasks, setTasks] = useState<ScheduledTask[]>([]);
  const [watches, setWatches] = useState<Watch[]>([]);
  const [task, setTask] = useState<TaskDraft | null>(null);
  const [watch, setWatch] = useState<{
    url: string;
    find: string;
    below: string;
    interval: number;
  } | null>(null);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [models, setModels] = useState<string[]>([]);
  const [defaultModel, setDefaultModel] = useState('');

  async function load() {
    const [t, w, m, s] = await Promise.all([
      api<ScheduledTask[]>('/tasks'),
      api<Watch[]>('/watches'),
      api<string[]>('/models').catch(() => [] as string[]),
      api<Settings>('/settings').catch(() => null),
    ]);
    setTasks(t);
    setWatches(w);
    setModels(m);
    // What "Default" means right now, as the scheduler resolves it.
    setDefaultModel(s?.defaultModel && m.includes(s.defaultModel) ? s.defaultModel : (m[0] ?? ''));
  }

  // The model a task runs with: its own while installed, else the default.
  function modelOf(t: ScheduledTask) {
    return t.model && models.includes(t.model) ? t.model : defaultModel;
  }

  useEffect(() => {
    if (open) {
      setError('');
      setNotice('');
      setTask(null);
      setWatch(null);
      load().catch((e) => setError(String(e)));
    }
  }, [open]);

  async function run(action: () => Promise<unknown>, message?: string) {
    setError('');
    setNotice('');
    try {
      await action();
      if (message) {
        setNotice(message);
      }
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  function taskBody(t: TaskDraft | ScheduledTask) {
    return {
      name: t.name,
      prompt: t.prompt,
      time: t.time,
      days: t.days,
      model: t.model || null,
      web: t.web,
      files: t.files,
      accounts: t.accounts,
      enabled: t.enabled,
    };
  }

  return (
    <Dialog title="Automations" open={open} wide onClose={onClose}>
      <div className="segmented" role="tablist" aria-label="Automations">
        <button role="tab" aria-selected={tab === 'tasks'} onClick={() => setTab('tasks')}>
          <Icon name="clock" size={15} />
          Scheduled
        </button>
        <button role="tab" aria-selected={tab === 'watches'} onClick={() => setTab('watches')}>
          <Icon name="eye" size={15} />
          Watches
        </button>
      </div>
      {notice && (
        <p className="notice-text" role="status">
          {notice}
        </p>
      )}
      {error && (
        <div role="alert" className="error">
          {error}
        </div>
      )}
      {tab === 'tasks' ? (
        <section className="automation-list" aria-label="Scheduled tasks">
          {tasks.map((t) => (
            <article key={t.id} className="automation-card">
              <div className="automation-head">
                <div className="automation-title">
                  <b>{t.name}</b>
                  <small>
                    {t.schedule} · next {when(t.nextRun)}
                    {modelOf(t) && ` · ${modelOf(t)}`}
                  </small>
                </div>
                <Switch
                  checked={t.enabled}
                  label={`${t.name} on`}
                  onChange={(enabled) =>
                    void run(() => send(`/tasks/${t.id}`, 'PUT', { ...taskBody(t), enabled }))
                  }
                />
              </div>
              <p className="automation-prompt">{t.prompt}</p>
              <div className="automation-actions">
                <button
                  className="secondary small"
                  onClick={() =>
                    void run(
                      () => send(`/tasks/${t.id}/run`, 'POST'),
                      `${t.name} is running. You'll get a notification.`,
                    )
                  }
                >
                  <Icon name="play" size={14} />
                  Run now
                </button>
                {t.conversationId && (
                  <button
                    className="secondary small"
                    onClick={() => onOpenConversation(t.conversationId!)}
                  >
                    Open results
                  </button>
                )}
                <button className="secondary small" onClick={() => setTask({ ...t })}>
                  Edit
                </button>
                <button
                  className="message-action"
                  aria-label={`Delete ${t.name}`}
                  onClick={() =>
                    window.confirm(`Delete ${t.name}?`) &&
                    void run(() => send(`/tasks/${t.id}`, 'DELETE'))
                  }
                >
                  <Icon name="trash" size={15} />
                </button>
              </div>
            </article>
          ))}
          {!tasks.length && <p className="hint">Nothing scheduled yet.</p>}
          {!task && (
            <div className="automation-add">
              {!tasks.some((t) => t.name === 'Morning brief') && (
                <button
                  className="primary"
                  onClick={() =>
                    void run(
                      () => send('/tasks/morning-brief', 'POST'),
                      'Morning brief added: weekdays at 07:00.',
                    )
                  }
                >
                  Add morning brief
                </button>
              )}
              {!tasks.some((t) => t.name === 'Konsertradar') && (
                <button
                  className="secondary"
                  onClick={() =>
                    void run(
                      () => send('/tasks/concert-radar', 'POST'),
                      'Concert radar added: Mondays at 09:00, only when there is something new.',
                    )
                  }
                >
                  <Icon name="music" size={15} />
                  Add concert radar
                </button>
              )}
              <button className="secondary" onClick={() => setTask({ ...emptyTask })}>
                New schedule
              </button>
            </div>
          )}
          {task && (
            <form
              className="account-form"
              onSubmit={(e) => {
                e.preventDefault();
                void run(async () => {
                  await send(
                    task.id ? `/tasks/${task.id}` : '/tasks',
                    task.id ? 'PUT' : 'POST',
                    taskBody(task),
                  );
                  setTask(null);
                });
              }}
            >
              <div className="field">
                <label htmlFor="task-name">Name</label>
                <input
                  id="task-name"
                  value={task.name}
                  onChange={(e) => setTask({ ...task, name: e.target.value })}
                />
              </div>
              <div className="field">
                <label htmlFor="task-prompt">What Leona should do</label>
                <textarea
                  id="task-prompt"
                  rows={3}
                  value={task.prompt}
                  placeholder="For example: Check my unread mail and tell me what needs an answer today."
                  onChange={(e) => setTask({ ...task, prompt: e.target.value })}
                />
              </div>
              <div className="field">
                <label htmlFor="task-time">Time</label>
                <input
                  id="task-time"
                  type="time"
                  value={task.time}
                  onChange={(e) => setTask({ ...task, time: e.target.value })}
                />
              </div>
              <fieldset className="day-picker">
                <legend>Days</legend>
                {dayNames.map((day, i) => (
                  <button
                    type="button"
                    key={i}
                    aria-label={dayLabels[i]}
                    aria-pressed={(task.days & (1 << i)) !== 0}
                    onClick={() => setTask({ ...task, days: task.days ^ (1 << i) })}
                  >
                    {day}
                  </button>
                ))}
              </fieldset>
              <div className="field">
                <label htmlFor="task-model">Model</label>
                <select
                  id="task-model"
                  value={task.model}
                  onChange={(e) => setTask({ ...task, model: e.target.value })}
                >
                  <option value="">Default{defaultModel ? ` (${defaultModel})` : ''}</option>
                  {task.model && !models.includes(task.model) && (
                    <option value={task.model}>{task.model} (not installed)</option>
                  )}
                  {models.map((name) => (
                    <option key={name}>{name}</option>
                  ))}
                </select>
              </div>
              <fieldset className="tool-checks">
                <legend>Tools</legend>
                {(['web', 'accounts', 'files'] as const).map((key) => (
                  <label key={key} className="checkbox">
                    <input
                      type="checkbox"
                      checked={task[key]}
                      onChange={(e) => setTask({ ...task, [key]: e.target.checked })}
                    />
                    {key === 'accounts'
                      ? 'Personal (mail, calendar, home)'
                      : key === 'web'
                        ? 'Web'
                        : 'Files'}
                  </label>
                ))}
              </fieldset>
              <div className="dialog-actions">
                <button type="button" className="secondary" onClick={() => setTask(null)}>
                  Cancel
                </button>
                <button
                  className="primary"
                  disabled={!task.name.trim() || !task.prompt.trim() || !task.days}
                >
                  Save
                </button>
              </div>
            </form>
          )}
        </section>
      ) : (
        <section className="automation-list" aria-label="Watches">
          {watches.map((w) => (
            <article key={w.id} className="automation-card">
              <div className="automation-head">
                <div className="automation-title">
                  <b>{w.name}</b>
                  <small>
                    Every{' '}
                    {w.intervalMinutes < 60
                      ? `${w.intervalMinutes} min`
                      : `${w.intervalMinutes / 60} h`}
                    {w.below != null && ` · below ${w.below}`} · checked {when(w.lastCheckedAt)}
                  </small>
                </div>
                <Switch
                  checked={w.enabled}
                  label={`${w.name} on`}
                  onChange={(enabled) =>
                    void run(() =>
                      send(`/watches/${w.id}`, 'PUT', {
                        name: w.name,
                        url: w.url,
                        find: w.find,
                        below: w.below,
                        intervalMinutes: w.intervalMinutes,
                        enabled,
                      }),
                    )
                  }
                />
              </div>
              <p className="automation-prompt">
                {w.lastError ? (
                  <span className="error-text">{w.lastError}</span>
                ) : (
                  (w.lastValue ?? 'Not checked yet')
                )}
              </p>
              <div className="automation-actions">
                <button
                  className="secondary small"
                  onClick={() => void run(() => send(`/watches/${w.id}/check`, 'POST'))}
                >
                  Check now
                </button>
                <a
                  className="secondary small"
                  href={w.url}
                  target="_blank"
                  rel="noopener noreferrer"
                >
                  Open page
                </a>
                <button
                  className="message-action"
                  aria-label={`Delete ${w.name}`}
                  onClick={() =>
                    window.confirm(`Stop watching ${w.name}?`) &&
                    void run(() => send(`/watches/${w.id}`, 'DELETE'))
                  }
                >
                  <Icon name="trash" size={15} />
                </button>
              </div>
            </article>
          ))}
          {!watches.length && (
            <p className="hint">
              Watch a price or a page and get a notification when it changes. You can also ask Leona
              in a chat with Personal on.
            </p>
          )}
          {watch ? (
            <form
              className="account-form"
              onSubmit={(e) => {
                e.preventDefault();
                void run(async () => {
                  await send('/watches', 'POST', {
                    url: watch.url,
                    find: watch.find,
                    below: watch.below ? Number(watch.below.replace(',', '.')) : null,
                    intervalMinutes: watch.interval,
                  });
                  setWatch(null);
                });
              }}
            >
              <div className="field">
                <label htmlFor="watch-url">Page</label>
                <input
                  id="watch-url"
                  type="url"
                  value={watch.url}
                  placeholder="https://…"
                  onChange={(e) => setWatch({ ...watch, url: e.target.value })}
                />
              </div>
              <div className="field">
                <label htmlFor="watch-find">Text before the value</label>
                <input
                  id="watch-find"
                  value={watch.find}
                  placeholder="For example Price"
                  onChange={(e) => setWatch({ ...watch, find: e.target.value })}
                />
              </div>
              <div className="field-grid">
                <div className="field">
                  <label htmlFor="watch-below">Notify below (optional)</label>
                  <input
                    id="watch-below"
                    inputMode="decimal"
                    value={watch.below}
                    onChange={(e) => setWatch({ ...watch, below: e.target.value })}
                  />
                </div>
                <div className="field">
                  <label htmlFor="watch-interval">Check every</label>
                  <select
                    id="watch-interval"
                    value={watch.interval}
                    onChange={(e) => setWatch({ ...watch, interval: Number(e.target.value) })}
                  >
                    {intervals.map((m) => (
                      <option key={m} value={m}>
                        {m < 60 ? `${m} minutes` : m === 60 ? 'hour' : `${m / 60} hours`}
                      </option>
                    ))}
                  </select>
                </div>
              </div>
              <div className="dialog-actions">
                <button type="button" className="secondary" onClick={() => setWatch(null)}>
                  Cancel
                </button>
                <button className="primary" disabled={!watch.url || !watch.find.trim()}>
                  Start watching
                </button>
              </div>
            </form>
          ) : (
            <div className="automation-add">
              <button
                className="primary"
                onClick={() => setWatch({ url: '', find: '', below: '', interval: 60 })}
              >
                New watch
              </button>
            </div>
          )}
        </section>
      )}
    </Dialog>
  );
}
