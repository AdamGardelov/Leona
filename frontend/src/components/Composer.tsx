import { useEffect, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { errorText, type Approval } from '../api';
import { describeSize, docBadge, documentTypes, type PendingAttachment } from '../attachments';
import { Icon, type IconName } from '../icons';
import { describeStep } from '../steps';
import { record, stopSpeaking, transcribe, unlockSpeech, type Recording } from '../voice';
import { ErrorAlert } from './ErrorAlert';
import { clock } from '../format';

export type Toggle = {
  label: string;
  icon: IconName;
  on: boolean;
  supported: boolean;
  hint: string;
  set: (on: boolean) => void;
};

function FilePreview({ content }: { content: string }) {
  return (
    <pre className="approval-preview">
      {content.split('\n').map((line, i) => (
        <span key={i}>
          <span className="line-number">{i + 1}</span>
          {line}
          {'\n'}
        </span>
      ))}
    </pre>
  );
}

function DiffPreview({ diff }: { diff: string }) {
  return (
    <pre className="approval-preview diff">
      {diff.split('\n').map((line, i) => {
        const kind = line.startsWith('+ ')
          ? 'added'
          : line.startsWith('- ')
            ? 'removed'
            : line.startsWith('@@') || line.startsWith('---') || line.startsWith('+++')
              ? 'meta'
              : '';
        return (
          <span key={i} className={kind}>
            {line}
            {'\n'}
          </span>
        );
      })}
    </pre>
  );
}

type ApprovalView = {
  icon: IconName;
  title: string;
  target: string;
  facts: string[];
  body: ReactNode;
  // The approve button's label; "Approve once" when not given.
  approve?: string;
};

type ApprovalCopy = { title: string; facts: string[]; approve?: string; icon?: IconName };

// The tools whose preview the backend builds (message, event, row, schedule, address or memory). The
// icon is the tool step's own unless given.
const approvalCopy: Record<string, ApprovalCopy> = {
  read_page: {
    title: 'Open this page?',
    facts: ['Asked because this chat read untrusted content'],
  },
  save_memory: { title: 'Remember this?', facts: ['Used in future chats'] },
  save_skill: {
    title: 'Save this skill?',
    facts: ['Used for similar requests', 'Edit it under Settings › Skills'],
  },
  mail_send: { title: 'Send this e-mail?', facts: ['Sent now from your mailbox'], approve: 'Send' },
  calendar_create: { title: 'Add this event?', facts: ['Added to your calendar'] },
  calendar_update: {
    title: 'Change this event?',
    facts: ['Updated in your calendar', 'Refused if the event changed first'],
    approve: 'Save change',
  },
  mail_manage: { title: 'Change these e-mails?', facts: ['Done in your mailbox'] },
  calendar_delete: {
    title: 'Delete this event?',
    facts: ['Removed from your calendar', 'Refused if the event changed first'],
    approve: 'Delete',
  },
  home_action: { title: 'Control this device?', facts: ['Runs in Home Assistant'] },
  record_expense: { title: 'Add this expense?', facts: ['Appended to the spreadsheet'] },
  schedule_task: { title: 'Create this schedule?', facts: ['You can turn it off in Automations'] },
  watch_page: { title: 'Watch this page?', facts: ['You can stop it in Automations'] },
};

// Deleting mail only moves it to the trash, and says so.
const trashCopy: ApprovalCopy = {
  title: 'Move to trash?',
  facts: ['Can be restored from the trash'],
  approve: 'Move to trash',
  icon: 'trash',
};

// What the user is approving, per tool. Unknown tools fall back to their raw arguments.
function describeApproval(approval: Approval): ApprovalView {
  const args = approval.arguments;
  const icon = describeStep(approval.toolName, args, 'awaiting_approval').icon;
  const path = String(args.path ?? '');
  const folder = args.folder ? `${String(args.folder)}/` : '';
  switch (approval.toolName) {
    case 'create_file': {
      const content = String(args.content ?? '');
      const lines = content.split('\n').length;
      return {
        icon,
        title: 'Leona wants to create a file',
        target: folder + path,
        facts: [
          'Creates a new file',
          'Never overwrites',
          `${lines} ${lines === 1 ? 'line' : 'lines'}`,
        ],
        body: <FilePreview content={content} />,
      };
    }
    case 'edit_file':
      return {
        icon,
        title: 'Leona wants to edit a file',
        target: folder + path,
        facts: ['Replaces exactly the text shown', 'Refused if the file changes first'],
        body: <DiffPreview diff={approval.preview ?? ''} />,
      };
    case 'run_command': {
      const [where, ...rest] = (approval.preview ?? '').split('\n');
      return {
        icon,
        title: 'Leona wants to run a command',
        target: where,
        facts: ['Runs as you, never with sudo', rest[rest.length - 1] ?? ''].filter(Boolean),
        body: <pre className="approval-preview command">{String(args.command ?? '')}</pre>,
        approve: 'Run once',
      };
    }
    default: {
      const known =
        approval.toolName === 'mail_manage' && args.action === 'delete'
          ? trashCopy
          : approvalCopy[approval.toolName];
      if (known && approval.preview) {
        return {
          ...known,
          icon: known.icon ?? icon,
          target: '',
          body: <pre className="approval-preview personal">{approval.preview}</pre>,
        };
      }
      return {
        icon: 'alert',
        title: `Leona wants to use ${approval.toolName}`,
        target: '',
        facts: [],
        body: <pre className="approval-preview">{JSON.stringify(args, null, 2)}</pre>,
      };
    }
  }
}

// The site an "Always allow" choice would trust, as the backend stores it.
function siteOf(url: unknown) {
  try {
    const host = new URL(String(url)).hostname.toLowerCase();
    return host.startsWith('www.') ? host.slice(4) : host;
  } catch {
    return null;
  }
}

function ApprovalCard({
  approval,
  deciding,
  onDecide,
}: {
  approval: Approval;
  deciding: boolean;
  onDecide: (approve: boolean, always?: boolean) => void;
}) {
  const view = describeApproval(approval);
  const site = approval.toolName === 'read_page' ? siteOf(approval.arguments.url) : null;
  return (
    <section className="approval" aria-labelledby="approval-title">
      <div className="approval-head">
        <span className="approval-icon">
          <Icon name={view.icon} size={20} />
        </span>
        <div>
          <h2 id="approval-title">{view.title}</h2>
          {view.target && <div className="mono approval-path">{view.target}</div>}
          {view.facts.length > 0 && (
            <div className="approval-facts">
              {view.facts.map((fact) => (
                <span key={fact}>{fact}</span>
              ))}
            </div>
          )}
        </div>
      </div>
      {view.body}
      <div className="approval-actions">
        <span>
          Single-use approval
          {approval.expiresAt && ` · expires ${clock(approval.expiresAt)}`}
        </span>
        <button
          type="button"
          className="secondary"
          disabled={deciding}
          onClick={() => onDecide(false)}
        >
          Decline
        </button>
        {site && (
          <button
            type="button"
            className="secondary always"
            disabled={deciding}
            title={`Open pages on ${site} without asking. Remove it under Settings › Trusted sites.`}
            onClick={() => onDecide(true, true)}
          >
            Always allow {site}
          </button>
        )}
        <button
          type="button"
          className="primary"
          disabled={deciding}
          onClick={() => onDecide(true)}
        >
          <Icon name="check" />
          {view.approve ?? 'Approve once'}
        </button>
      </div>
    </section>
  );
}

type AttachSheetProps = {
  open: boolean;
  busy: boolean;
  toggles: Toggle[];
  models: string[];
  model: string;
  setModel: (model: string) => void;
  onFiles: (files: File[]) => void;
  onClose: () => void;
};

// Phones: one sheet for the camera, photos, files, the model and this message's tools.
// Where a file can come from on a phone; each opens its own picker.
const attachSources: {
  icon: IconName;
  label: string;
  accept: string;
  capture?: 'environment';
  multiple?: boolean;
}[] = [
  { icon: 'camera', label: 'Camera', accept: 'image/*', capture: 'environment' },
  { icon: 'image', label: 'Photos', accept: 'image/*', multiple: true },
  { icon: 'paperclip', label: 'Files', accept: `${documentTypes},image/*`, multiple: true },
];

function AttachTile({
  source,
  onPick,
}: {
  source: (typeof attachSources)[number];
  onPick: (input: HTMLInputElement) => void;
}) {
  const input = useRef<HTMLInputElement>(null);
  return (
    <>
      <button type="button" className="attach-tile" onClick={() => input.current?.click()}>
        <Icon name={source.icon} size={24} />
        {source.label}
      </button>
      <input
        ref={input}
        type="file"
        accept={source.accept}
        capture={source.capture}
        multiple={source.multiple}
        hidden
        onChange={(e) => onPick(e.currentTarget)}
      />
    </>
  );
}

export function AttachSheet(props: AttachSheetProps) {
  useEffect(() => {
    if (!props.open) {
      return;
    }
    function onKeyDown(event: KeyboardEvent) {
      if (event.key === 'Escape') {
        props.onClose();
      }
    }
    document.addEventListener('keydown', onKeyDown);
    return () => {
      document.removeEventListener('keydown', onKeyDown);
    };
  }, [props.open]);

  function picked(input: HTMLInputElement | null) {
    if (input?.files?.length) {
      props.onFiles(Array.from(input.files));
      input.value = '';
      props.onClose();
    }
  }

  if (!props.open) {
    return null;
  }
  return (
    <>
      <div className="sheet-backdrop" onClick={props.onClose} />
      <section className="bottom-sheet" role="dialog" aria-modal="true" aria-label="Add to message">
        <span className="sheet-handle" aria-hidden="true" />
        <div className="attach-tiles">
          {attachSources.map((source) => (
            <AttachTile key={source.label} source={source} onPick={picked} />
          ))}
        </div>
        <label className="sheet-model">
          <span>Model</span>
          <select
            value={props.model}
            disabled={props.busy || !props.models.length}
            onChange={(e) => props.setModel(e.target.value)}
          >
            {props.models.map((name) => (
              <option key={name}>{name}</option>
            ))}
          </select>
        </label>
        <h2 className="label">Tools for this message</h2>
        <div className="switch-list">
          {props.toggles.map((t) => (
            <button
              type="button"
              key={t.label}
              role="switch"
              aria-checked={t.on && t.supported}
              disabled={props.busy || !t.supported}
              className="switch-row"
              onClick={() => t.set(!t.on)}
            >
              <span className="switch-icon">
                <Icon name={t.icon} size={17} />
              </span>
              <span className="switch-text">
                <b>{t.label}</b>
                <small>{t.supported ? t.hint : 'Not supported by this model'}</small>
              </span>
              <span className="switch" aria-hidden="true" />
            </button>
          ))}
        </div>
        <button type="button" className="primary sheet-done" onClick={props.onClose}>
          Done
        </button>
      </section>
    </>
  );
}

function AttachmentStrip({
  attachments,
  onRemove,
}: {
  attachments: PendingAttachment[];
  onRemove: (key: string) => void;
}) {
  if (!attachments.length) {
    return null;
  }
  return (
    <ul className="attachment-strip" aria-label="Attachments">
      {attachments.map((a) => (
        <li key={a.key} className={`attachment ${a.kind} ${a.status}`} title={a.error ?? a.name}>
          {a.kind === 'image' && a.previewUrl ? (
            <img src={a.previewUrl} alt={a.name} />
          ) : (
            <span className="attachment-doc">
              <span className="doc-badge">{docBadge(a.name)}</span>
              <span className="doc-text">
                <b>{a.name}</b>
                <small>{a.status === 'error' ? 'Failed' : describeSize(a.size)}</small>
              </span>
            </span>
          )}
          {a.status === 'uploading' && <span className="attachment-busy" aria-label="Uploading" />}
          <button
            type="button"
            className="attachment-remove"
            aria-label={`Remove ${a.name}`}
            onClick={() => onRemove(a.key)}
          >
            <Icon name="close" size={12} />
          </button>
        </li>
      ))}
    </ul>
  );
}

type ComposerProps = {
  text: string;
  setText: (text: string) => void;
  busy: boolean;
  disabled: boolean;
  canSend: boolean;
  canStop: boolean;
  toggles: Toggle[];
  attachments: PendingAttachment[];
  approval: Approval | null;
  deciding: boolean;
  error: string;
  onSend: () => void;
  onStop: () => void;
  onDecide: (approve: boolean, always?: boolean) => void;
  onFiles: (files: File[]) => void;
  onRemoveAttachment: (key: string) => void;
  onOpenSheet: () => void;
  // Messages written while Leona answers; each goes out when the answer before it is done.
  queued: QueuedMessage[];
  onSendQueued: (item: QueuedMessage) => void;
  onRemoveQueued: (item: QueuedMessage) => void;
  // Speech to text on the computer; null hides the microphone, a string explains why it cannot be used.
  voice: true | string | null;
  onVoice: (text: string) => void;
};

type QueuedMessage = { key: number; text: string };

const phone = () => window.matchMedia('(max-width: 650px)').matches;
const touch = () => window.matchMedia('(pointer: coarse)').matches;

export function Composer(props: ComposerProps) {
  const picker = useRef<HTMLInputElement>(null);
  const field = useRef<HTMLTextAreaElement>(null);
  const recording = useRef<Recording | null>(null);
  const [listening, setListening] = useState<'idle' | 'recording' | 'transcribing'>('idle');
  const [level, setLevel] = useState(0);
  const [seconds, setSeconds] = useState(0);
  const [voiceError, setVoiceError] = useState('');

  useEffect(() => {
    if (listening !== 'recording') {
      return;
    }
    const started = Date.now();
    const timer = window.setInterval(
      () => setSeconds(Math.floor((Date.now() - started) / 1000)),
      250,
    );
    return () => window.clearInterval(timer);
  }, [listening]);

  // Cancels a recording left running when the composer goes away.
  useEffect(() => () => recording.current?.cancel(), []);

  async function startListening() {
    setVoiceError('');
    stopSpeaking();
    // A tap is the only moment iOS allows a page to start speaking later.
    unlockSpeech();
    try {
      setSeconds(0);
      recording.current = await record(
        (value) => setLevel(value),
        () => void finishListening(),
      );
      setListening('recording');
    } catch {
      setVoiceError('Leona could not use the microphone. Allow it in the browser settings.');
      setListening('idle');
    }
  }

  async function finishListening() {
    const current = recording.current;
    recording.current = null;
    if (!current) {
      return;
    }
    setListening('transcribing');
    try {
      const audio = await current.stop();
      const text = audio ? await transcribe(audio) : '';
      if (text) {
        props.onVoice(text);
      } else {
        setVoiceError('Leona heard nothing. Hold the phone closer and try again.');
      }
    } catch (e) {
      setVoiceError(errorText(e));
    } finally {
      setListening('idle');
      setLevel(0);
    }
  }

  function cancelListening() {
    recording.current?.cancel();
    recording.current = null;
    setListening('idle');
    setLevel(0);
  }

  // The message field starts at one line and grows with the text up to its maximum height.
  useLayoutEffect(() => {
    const element = field.current;
    if (!element) {
      return;
    }
    element.style.height = 'auto';
    element.style.height = `${element.scrollHeight}px`;
  }, [props.text]);
  const [dragging, setDragging] = useState(false);
  const active = props.toggles.filter((t) => t.on && t.supported);

  return (
    <div
      className={dragging ? 'composer-area dragging' : 'composer-area'}
      onDragOver={(e) => {
        if (e.dataTransfer.types.includes('Files')) {
          e.preventDefault();
          setDragging(true);
        }
      }}
      onDragLeave={(e) => {
        if (e.currentTarget === e.target) {
          setDragging(false);
        }
      }}
      onDrop={(e) => {
        if (e.dataTransfer.files.length) {
          e.preventDefault();
          props.onFiles(Array.from(e.dataTransfer.files));
        }
        setDragging(false);
      }}
    >
      {props.approval && (
        <>
          <div className="sheet-backdrop approval-backdrop" />
          <ApprovalCard
            approval={props.approval}
            deciding={props.deciding}
            onDecide={props.onDecide}
          />
        </>
      )}
      <ErrorAlert error={props.error} />
      {props.queued.length > 0 && (
        <ul className="queue" aria-label="Waiting to be sent">
          {props.queued.map((item) => (
            <li key={item.key}>
              <Icon name="clock" size={14} />
              <span className="queue-text">{item.text || 'Attachments'}</span>
              {props.busy ? (
                <span className="queue-when">Next</span>
              ) : (
                <button
                  type="button"
                  className="message-action"
                  aria-label={`Send now: ${item.text}`}
                  title="Send now"
                  onClick={() => props.onSendQueued(item)}
                >
                  <Icon name="send" size={15} />
                </button>
              )}
              <button
                type="button"
                className="message-action"
                aria-label={`Remove from the queue: ${item.text}`}
                title="Remove"
                onClick={() => props.onRemoveQueued(item)}
              >
                <Icon name="close" size={15} />
              </button>
            </li>
          ))}
        </ul>
      )}
      <form
        onSubmit={(e) => {
          e.preventDefault();
          props.onSend();
        }}
      >
        <AttachmentStrip attachments={props.attachments} onRemove={props.onRemoveAttachment} />
        <textarea
          ref={field}
          aria-label="Message"
          placeholder={props.busy ? 'Write your next message…' : 'Message Leona…'}
          rows={1}
          value={props.text}
          disabled={props.disabled}
          onChange={(e) => props.setText(e.target.value)}
          onPaste={(e) => {
            const files = Array.from(e.clipboardData.files);
            if (files.length) {
              props.onFiles(files);
              if (!e.clipboardData.getData('text/plain')) {
                e.preventDefault();
              }
            }
          }}
          onKeyDown={(e) => {
            // On touch keyboards Enter makes a new line; the send button sends.
            if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing && !touch()) {
              e.preventDefault();
              props.onSend();
            }
          }}
        />
        {listening !== 'idle' && (
          <div className="listening" role="status">
            {listening === 'recording' ? (
              <>
                <span
                  className="listening-dot"
                  style={{ transform: `scale(${1 + Math.min(level * 4, 0.8)})` }}
                />
                <span>
                  Listening… {Math.floor(seconds / 60)}:{String(seconds % 60).padStart(2, '0')}
                </span>
                <button type="button" className="link-button" onClick={cancelListening}>
                  Cancel
                </button>
                <button
                  type="button"
                  className="listening-done"
                  onClick={() => void finishListening()}
                >
                  <Icon name="check" size={16} />
                  Done
                </button>
              </>
            ) : (
              <>
                <span className="pulse" />
                <span>Turning speech into text…</span>
              </>
            )}
          </div>
        )}
        {voiceError && listening === 'idle' && (
          <div className="listening voice-error" role="alert">
            <span>{voiceError}</span>
            <button type="button" className="link-button" onClick={() => setVoiceError('')}>
              OK
            </button>
          </div>
        )}
        <div className="controls">
          <button
            type="button"
            className="attach-button"
            aria-label={phone() ? 'Add photos, files or tools' : 'Attach files'}
            title="Attach images or documents"
            disabled={props.disabled}
            onClick={() => (phone() ? props.onOpenSheet() : picker.current?.click())}
          >
            <Icon name={phone() ? 'plus' : 'paperclip'} size={18} />
          </button>
          <input
            ref={picker}
            type="file"
            multiple
            hidden
            accept={`${documentTypes},image/*`}
            onChange={(e) => {
              if (e.currentTarget.files?.length) {
                props.onFiles(Array.from(e.currentTarget.files));
                e.currentTarget.value = '';
              }
            }}
          />
          {props.toggles.map((t) => (
            <button
              type="button"
              key={t.label}
              className="chip desktop-only"
              aria-pressed={t.on && t.supported}
              disabled={!t.supported}
              title={t.supported ? t.hint : 'Not supported by this model'}
              onClick={() => t.set(!t.on)}
            >
              <Icon name={t.icon} size={14} />
              {t.label}
            </button>
          ))}
          {active.map((t) => (
            <button
              type="button"
              key={t.label}
              className={
                active.length >= 3 ? 'tool-pill mobile-only compact' : 'tool-pill mobile-only'
              }
              aria-label={`${t.label} on. Turn off`}
              title={t.label}
              onClick={() => t.set(false)}
            >
              <Icon name={t.icon} size={13} />
              <span className="tool-pill-label">{t.label}</span>
            </button>
          ))}
          {props.voice !== null && listening === 'idle' && (
            <button
              type="button"
              className="mic"
              aria-label="Speak"
              title={props.voice === true ? 'Speak your message' : props.voice}
              disabled={props.disabled}
              onClick={() =>
                props.voice === true ? void startListening() : setVoiceError(String(props.voice))
              }
            >
              <Icon name="mic" size={18} />
            </button>
          )}
          {props.busy && (
            <button
              type="button"
              className="stop"
              onClick={props.onStop}
              disabled={!props.canStop}
              aria-label="Stop"
            >
              <svg width="12" height="12" viewBox="0 0 24 24" aria-hidden="true">
                <rect x="4" y="4" width="16" height="16" rx="3" fill="currentColor" />
              </svg>
              <span className="stop-label">Stop</span>
            </button>
          )}
          {(!props.busy || props.canSend) && (
            <button
              className={props.busy ? 'send queue-send' : 'send'}
              aria-label={props.busy ? 'Send when Leona is done' : 'Send'}
              title={props.busy ? 'Send when Leona is done' : undefined}
              disabled={!props.canSend}
            >
              <Icon name="send" size={18} />
            </button>
          )}
        </div>
      </form>
      <div className="footer">
        Local model · Enter to send · Shift + Enter for a new line · Paste or drop files to attach
      </div>
    </div>
  );
}
