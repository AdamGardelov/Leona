import { memo, useRef, useState, type MutableRefObject } from 'react';
import ReactMarkdown, { type Components } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import type { AttachmentRef, Message, ToolStep } from '../api';
import { describeSize, docBadge } from '../attachments';
import { CodeBlock, CopyButton, DraftCard, MailStepCard } from './CodeBlock';
import { ImageViewerProvider, useOpenImage } from './ImageViewer';
import { MailList } from './MailList';
import { Icon, Mark, type IconName } from '../icons';
import { describeStep, statusLabels } from '../steps';
import { seconds } from '../format';

const statusIcons: Partial<Record<ToolStep['status'], IconName>> = {
  completed: 'check',
  failed: 'alert',
  rejected: 'close',
  expired: 'clock',
  unavailable: 'close',
  awaiting_approval: 'clock',
};

function Step({ step }: { step: ToolStep }) {
  const label = describeStep(step.name, step.arguments, step.status);
  const statusIcon = statusIcons[step.status];
  return (
    <li className={`tool-step ${step.status}`}>
      <span className="tool-icon">
        <Icon name={label.icon} size={14} />
      </span>
      <span className="tool-title">{label.title}</span>
      {label.detail && <span className="tool-detail mono">{label.detail}</span>}
      <span className="tool-status">
        {step.status === 'running' ? (
          <span className="pulse" />
        ) : (
          statusIcon && <Icon name={statusIcon} size={14} />
        )}
        <span>
          {step.status === 'completed' && step.summary ? step.summary : statusLabels[step.status]}
          {step.durationMs !== undefined && step.durationMs >= 1000 && (
            <> · {seconds(step.durationMs)}</>
          )}
        </span>
      </span>
    </li>
  );
}

function MessageAttachments({ attachments }: { attachments: AttachmentRef[] }) {
  const openImage = useOpenImage();
  return (
    <div className="message-attachments">
      {attachments.map((a) =>
        a.kind === 'image' ? (
          <button
            type="button"
            key={a.id}
            className="message-image"
            aria-label={`Open ${a.name}`}
            onClick={() => openImage(a)}
          >
            <img src={`/api/uploads/${a.id}`} alt={a.name} loading="lazy" />
          </button>
        ) : (
          <a
            key={a.id}
            className="message-doc"
            href={`/api/uploads/${a.id}`}
            target="_blank"
            rel="noopener noreferrer"
          >
            <span className="doc-badge">{docBadge(a.name)}</span>
            <span className="doc-text">
              <b>{a.name}</b>
              <small>{describeSize(a.size)}</small>
            </span>
          </a>
        ),
      )}
    </div>
  );
}

// Fenced code becomes a labelled block with a copy button; draft blocks become a message card.
const markdownComponents: Components = {
  img: () => null,
  a: ({ children, href }) => (
    <a href={href} target="_blank" rel="noopener noreferrer">
      {children}
    </a>
  ),
  pre: ({ node, children }) => {
    const code = node?.children[0];
    if (code?.type !== 'element' || code.tagName !== 'code') {
      return <pre>{children}</pre>;
    }
    const classes = code.properties.className;
    const language = Array.isArray(classes)
      ? (classes
          .map(String)
          .find((c) => c.startsWith('language-'))
          ?.slice(9) ?? '')
      : '';
    const text = code.children.map((child) => (child.type === 'text' ? child.value : '')).join('');
    return <CodeBlock lang={language} code={text.replace(/\n$/, '')} />;
  },
};
const remarkPlugins = [remarkGfm];

type ThreadProps = {
  messages: Message[];
  busy: boolean;
  currentStatus?: string;
  onEdit: (message: Message, text: string) => void;
  onRegenerate: (reply: Message) => void;
  onContinue: () => void;
  onInspect?: () => void;
  onSaveSkill?: () => void;
  // Reads a reply aloud; speaking is the index of the reply being read.
  onSpeak?: (index: number) => void;
  speaking?: number | null;
};

type ReplyProps = {
  m: Message;
  index: number;
  busy: boolean;
  // The answer being written right now, and what Leona is doing for it.
  live: boolean;
  status?: string;
  isLast: boolean;
  speaking: boolean;
  canInspect: boolean;
  canSaveSkill: boolean;
  canSpeak: boolean;
  // Always the same object, so a finished reply is not drawn again for every new word of the live one.
  handlers: MutableRefObject<ThreadProps>;
};

const Reply = memo(function Reply({
  m,
  index,
  busy,
  live,
  status,
  isLast,
  speaking,
  canInspect,
  canSaveSkill,
  canSpeak,
  handlers,
}: ReplyProps) {
  const canAct = isLast && !busy;
  const actions = [
    {
      shown: canAct,
      icon: 'regenerate' as IconName,
      label: 'Regenerate answer',
      title: 'Regenerate',
      onClick: () => handlers.current.onRegenerate(m),
    },
    {
      shown: canAct && canSaveSkill,
      icon: 'bolt' as IconName,
      label: 'Save as skill',
      title: 'Save as skill',
      onClick: () => handlers.current.onSaveSkill?.(),
    },
    {
      shown: canAct && canInspect,
      icon: 'inspect' as IconName,
      label: 'Inspect run',
      title: 'Inspect run',
      onClick: () => handlers.current.onInspect?.(),
    },
  ].filter((a) => a.shown);
  // The skill a reply followed is shown by its name next to the model, not as a step.
  const skill = m.tools?.find((step) => step.name === 'use_skill');
  const skillName = typeof skill?.arguments.name === 'string' ? skill.arguments.name : '';
  const steps = m.tools?.filter((step) => step !== skill) ?? [];
  return (
    <article className="assistant">
      <div className="label">
        <Mark size={16} />
        Leona
        {m.model && <span className="label-model">{m.model}</span>}
        {skillName && (
          <span className="label-skill" title={`Followed the skill “${skillName}”`}>
            <Icon name="bolt" size={12} />
            <span className="visually-hidden">Skill:</span>
            <span className="label-skill-name">{skillName}</span>
          </span>
        )}
      </div>
      {steps.length > 0 && (
        <ol className="tool-steps" aria-label="Tool steps">
          {steps.map((step, i) => (
            <Step key={step.id ?? i} step={step} />
          ))}
        </ol>
      )}
      {m.tools?.map(
        (step, i) => step.mails && <MailList key={step.id ?? `list-${i}`} items={step.mails} />,
      )}
      {m.attachments && m.attachments.length > 0 && (
        <MessageAttachments attachments={m.attachments} />
      )}
      {m.tools?.map((step, i) =>
        step.name === 'mail_send' ? (
          <MailStepCard key={step.id ?? `mail-${i}`} step={step} />
        ) : (
          step.drafts?.map((draft, n) => (
            <DraftCard
              key={`${step.id ?? i}-${n}`}
              text={draft}
              title={step.name === 'mail_draft' ? 'Email draft · saved in Drafts' : 'Email draft'}
            />
          ))
        ),
      )}
      {live && !m.content && (
        <div className="step" role="status">
          <span className="pulse" />
          {status ?? 'Working…'}
        </div>
      )}
      {m.thinking && (
        <details>
          <summary>Thinking</summary>
          <p>{m.thinking}</p>
        </details>
      )}
      {(m.content || !busy) && (
        <div className="markdown">
          <ReactMarkdown remarkPlugins={remarkPlugins} components={markdownComponents}>
            {m.content || 'No answer received.'}
          </ReactMarkdown>
        </div>
      )}
      {m.truncated && (
        <div className="notice warning" role="note">
          <Icon name="alert" size={15} />
          <span>The answer hit the output limit and may end mid-sentence.</span>
          {canAct && (
            <button className="link-button" onClick={() => handlers.current.onContinue()}>
              Continue
            </button>
          )}
        </div>
      )}
      {m.complete === false && (
        <div className="notice warning" role="note">
          <Icon name="alert" size={15} />
          <span>Response interrupted.</span>
          {canAct && (
            <button className="link-button" onClick={() => handlers.current.onRegenerate(m)}>
              Retry
            </button>
          )}
        </div>
      )}
      {!live && m.content && (
        <div className="message-actions">
          <CopyButton text={m.content} />
          {canSpeak && (
            <button
              className={speaking ? 'message-action active' : 'message-action'}
              aria-label={speaking ? 'Stop reading' : 'Read aloud'}
              title={speaking ? 'Stop reading' : 'Read aloud'}
              onClick={() => handlers.current.onSpeak?.(index)}
            >
              <Icon name="speaker" size={15} />
            </button>
          )}
          {actions.map((action) => (
            <button
              key={action.label}
              className="message-action"
              aria-label={action.label}
              title={action.title}
              onClick={action.onClick}
            >
              <Icon name={action.icon} size={15} />
            </button>
          ))}
        </div>
      )}
    </article>
  );
});

export function Thread(props: ThreadProps) {
  const { messages, busy, currentStatus, onEdit, speaking } = props;
  const handlers = useRef(props);
  handlers.current = props;
  const [editing, setEditing] = useState<number | null>(null);
  const [draft, setDraft] = useState('');
  const lastReply = messages.reduce((last, m, i) => (m.role !== 'user' ? i : last), -1);

  return (
    <ImageViewerProvider>
      <div className="thread">
        {messages.map((m, i) => {
          if (m.role !== 'user') {
            const live = busy && i === messages.length - 1;
            return (
              <Reply
                key={m.id ?? `live-${i}`}
                m={m}
                index={i}
                busy={busy}
                live={live}
                status={live ? currentStatus : undefined}
                isLast={i === lastReply}
                speaking={speaking === i}
                canInspect={!!props.onInspect}
                canSaveSkill={!!props.onSaveSkill}
                canSpeak={!!props.onSpeak}
                handlers={handlers}
              />
            );
          }
          const isEditing = editing === m.id && m.id !== undefined;
          return (
            <article className="user" key={m.id ?? `live-${i}`}>
              {isEditing ? (
                <form
                  className="edit-form"
                  onSubmit={(e) => {
                    e.preventDefault();
                    if (draft.trim()) {
                      setEditing(null);
                      onEdit(m, draft.trim());
                    }
                  }}
                >
                  <textarea
                    aria-label="Edit message"
                    value={draft}
                    rows={Math.min(8, Math.max(2, draft.split('\n').length))}
                    autoFocus
                    onChange={(e) => setDraft(e.target.value)}
                    onKeyDown={(e) => {
                      if (e.key === 'Escape') {
                        setEditing(null);
                      }
                    }}
                  />
                  <p>Sending replaces this message and every reply after it.</p>
                  <div className="edit-actions">
                    <button type="button" className="secondary" onClick={() => setEditing(null)}>
                      Cancel
                    </button>
                    <button className="primary" disabled={!draft.trim()}>
                      Send
                    </button>
                  </div>
                </form>
              ) : (
                <>
                  {m.attachments && m.attachments.length > 0 && (
                    <MessageAttachments attachments={m.attachments} />
                  )}
                  {m.content && <div className="bubble">{m.content}</div>}
                  {!busy && m.id !== undefined && (
                    <div className="message-actions">
                      <button
                        className="message-action"
                        aria-label="Edit message"
                        title="Edit"
                        onClick={() => {
                          setDraft(m.content);
                          setEditing(m.id ?? null);
                        }}
                      >
                        <Icon name="pencil" size={15} />
                      </button>
                      <CopyButton text={m.content} />
                    </div>
                  )}
                </>
              )}
            </article>
          );
        })}
      </div>
    </ImageViewerProvider>
  );
}
