import { useState } from 'react';
import ReactMarkdown, { type Components } from 'react-markdown';
import remarkGfm from 'remark-gfm';
import type { AttachmentRef, Message, ToolStep } from '../api';
import { describeSize } from '../attachments';
import { CodeBlock, CopyButton, DraftCard, MailStepCard } from './CodeBlock';
import { MailList } from './MailList';
import { Icon, Mark, type IconName } from '../icons';
import { describeStep, statusLabels } from '../steps';

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
            <> · {(step.durationMs / 1000).toFixed(1)} s</>
          )}
        </span>
      </span>
    </li>
  );
}

function MessageAttachments({ attachments }: { attachments: AttachmentRef[] }) {
  return (
    <div className="message-attachments">
      {attachments.map((a) =>
        a.kind === 'image' ? (
          <a
            key={a.id}
            className="message-image"
            href={`/api/uploads/${a.id}`}
            target="_blank"
            rel="noopener noreferrer"
          >
            <img src={`/api/uploads/${a.id}`} alt={a.name} loading="lazy" />
          </a>
        ) : (
          <a
            key={a.id}
            className="message-doc"
            href={`/api/uploads/${a.id}`}
            target="_blank"
            rel="noopener noreferrer"
          >
            <span className="doc-badge">{a.name.split('.').pop()?.slice(0, 4).toUpperCase()}</span>
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

export function Thread({
  messages,
  busy,
  currentStatus,
  onEdit,
  onRegenerate,
  onContinue,
  onInspect,
  onSaveSkill,
  onSpeak,
  speaking,
}: ThreadProps) {
  const [editing, setEditing] = useState<number | null>(null);
  const [draft, setDraft] = useState('');
  const lastReply = messages.reduce((last, m, i) => (m.role !== 'user' ? i : last), -1);

  return (
    <div className="thread">
      {messages.map((m, i) => {
        if (m.role === 'user') {
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
        }
        const live = busy && i === messages.length - 1;
        const isLast = i === lastReply;
        return (
          <article className="assistant" key={m.id ?? `live-${i}`}>
            <div className="label">
              <Mark size={16} />
              Leona
              {m.model && <span className="label-model">{m.model}</span>}
            </div>
            {m.tools && m.tools.length > 0 && (
              <ol className="tool-steps" aria-label="Tool steps">
                {m.tools.map((step, index) => (
                  <Step key={step.id ?? index} step={step} />
                ))}
              </ol>
            )}
            {m.tools?.map(
              (step, index) =>
                step.mails && <MailList key={step.id ?? `list-${index}`} items={step.mails} />,
            )}
            {m.tools?.map((step, index) =>
              step.name === 'mail_send' ? (
                <MailStepCard key={step.id ?? `mail-${index}`} step={step} />
              ) : (
                step.drafts?.map((draft, n) => (
                  <DraftCard
                    key={`${step.id ?? index}-${n}`}
                    text={draft}
                    title={
                      step.name === 'mail_draft' ? 'Email draft · saved in Drafts' : 'Email draft'
                    }
                  />
                ))
              ),
            )}
            {live && !m.content && (
              <div className="step" role="status">
                <span className="pulse" />
                {currentStatus ?? 'Working…'}
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
                <ReactMarkdown remarkPlugins={[remarkGfm]} components={markdownComponents}>
                  {m.content || 'No answer received.'}
                </ReactMarkdown>
              </div>
            )}
            {m.truncated && (
              <div className="notice warning" role="note">
                <Icon name="alert" size={15} />
                <span>The answer hit the output limit and may end mid-sentence.</span>
                {isLast && !busy && (
                  <button className="link-button" onClick={onContinue}>
                    Continue
                  </button>
                )}
              </div>
            )}
            {m.complete === false && (
              <div className="notice warning" role="note">
                <Icon name="alert" size={15} />
                <span>Response interrupted.</span>
                {isLast && !busy && (
                  <button className="link-button" onClick={() => onRegenerate(m)}>
                    Retry
                  </button>
                )}
              </div>
            )}
            {!live && m.content && (
              <div className="message-actions">
                <CopyButton text={m.content} />
                {onSpeak && (
                  <button
                    className={speaking === i ? 'message-action active' : 'message-action'}
                    aria-label={speaking === i ? 'Stop reading' : 'Read aloud'}
                    title={speaking === i ? 'Stop reading' : 'Read aloud'}
                    onClick={() => onSpeak(i)}
                  >
                    <Icon name="speaker" size={15} />
                  </button>
                )}
                {isLast && !busy && (
                  <button
                    className="message-action"
                    aria-label="Regenerate answer"
                    title="Regenerate"
                    onClick={() => onRegenerate(m)}
                  >
                    <Icon name="regenerate" size={15} />
                  </button>
                )}
                {isLast && !busy && onSaveSkill && (
                  <button
                    className="message-action"
                    aria-label="Save as skill"
                    title="Save as skill"
                    onClick={onSaveSkill}
                  >
                    <Icon name="bolt" size={15} />
                  </button>
                )}
                {isLast && !busy && onInspect && (
                  <button
                    className="message-action"
                    aria-label="Inspect run"
                    title="Inspect run"
                    onClick={onInspect}
                  >
                    <Icon name="inspect" size={15} />
                  </button>
                )}
              </div>
            )}
          </article>
        );
      })}
    </div>
  );
}
