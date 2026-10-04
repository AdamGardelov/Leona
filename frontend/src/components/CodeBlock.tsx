import { useState } from 'react';
import type { ToolStep } from '../api';
import { highlight, languageName } from '../highlight';
import { Icon } from '../icons';

// Plain-text copy that also works over http on the home network, where the clipboard API is missing.
async function copyText(text: string) {
  if (navigator.clipboard && window.isSecureContext) {
    await navigator.clipboard.writeText(text);
    return;
  }
  const area = document.createElement('textarea');
  area.value = text;
  area.setAttribute('readonly', '');
  area.style.position = 'fixed';
  area.style.opacity = '0';
  document.body.append(area);
  area.select();
  document.execCommand('copy');
  area.remove();
}

export function CopyButton({
  text,
  label = 'Copy message',
  showLabel = false,
}: {
  text: string;
  label?: string;
  showLabel?: boolean;
}) {
  const [copied, setCopied] = useState(false);
  return (
    <button
      type="button"
      className={showLabel ? 'message-action copy-labelled' : 'message-action'}
      aria-label={copied ? 'Copied' : label}
      title={copied ? 'Copied' : 'Copy'}
      onClick={() => {
        void copyText(text).then(() => {
          setCopied(true);
          setTimeout(() => setCopied(false), 1500);
        });
      }}
    >
      <Icon name={copied ? 'check' : 'copy'} size={15} />
      {showLabel && <span>{copied ? 'Copied' : 'Copy'}</span>}
    </button>
  );
}

// Fenced blocks marked draft, email or message are text for the user to send, not code.
const draftLanguages = new Set(['draft', 'email', 'e-mail', 'mail', 'message', 'sms']);
const fieldPattern = /^(to|till|cc|bcc|from|från|subject|ämne)\s*:\s*(.*)$/i;
const fieldNames: Record<string, string> = {
  to: 'To',
  till: 'To',
  cc: 'Cc',
  bcc: 'Bcc',
  from: 'From',
  från: 'From',
  subject: 'Subject',
  ämne: 'Subject',
};

// Leading "To:" and "Subject:" lines become fields; the rest is the message.
function parseDraft(text: string) {
  const lines = text.split('\n');
  const fields: { name: string; value: string }[] = [];
  let index = 0;
  for (; index < lines.length; index++) {
    const match = lines[index].match(fieldPattern);
    if (!match) {
      break;
    }
    fields.push({ name: fieldNames[match[1].toLowerCase()], value: match[2].trim() });
  }
  while (index < lines.length && !lines[index].trim()) {
    index++;
  }
  return { fields, body: lines.slice(index).join('\n').trimEnd() };
}

export function DraftCard({ text, title }: { text: string; title?: string }) {
  const { fields, body } = parseDraft(text);
  const email = fields.some((f) => f.name === 'To' || f.name === 'Subject');
  return (
    <figure className="draft-card">
      <figcaption className="block-head">
        <span className="block-label">
          <Icon name={email ? 'mail' : 'pencil'} size={14} />
          {title ?? (email ? 'Email draft' : 'Draft')}
        </span>
        <CopyButton text={body} label="Copy the text" showLabel />
      </figcaption>
      {fields.length > 0 && (
        <dl className="draft-fields">
          {fields.map((field, i) => (
            <div key={i}>
              <dt>{field.name}</dt>
              <dd>{field.value}</dd>
            </div>
          ))}
        </dl>
      )}
      <div className="draft-body">{body}</div>
    </figure>
  );
}

// A sent e-mail, shown exactly as the tool call wrote it rather than as the model retells it. Drafts
// come with the tool's result instead (ToolStep.drafts), so they show what the mailbox holds.
export function MailStepCard({ step }: { step: ToolStep }) {
  const { to, cc, subject, body } = step.arguments;
  if (
    step.status !== 'completed' ||
    typeof to !== 'string' ||
    typeof subject !== 'string' ||
    typeof body !== 'string'
  ) {
    return null;
  }
  const text = [`To: ${to}`, typeof cc === 'string' && cc ? `Cc: ${cc}` : '', `Subject: ${subject}`]
    .filter(Boolean)
    .join('\n');
  return <DraftCard text={`${text}\n\n${body}`} title="Sent email" />;
}

// A job from the job radar, written by the model as "Key: value" lines in a job block.
const jobKeys: Record<string, string> = {
  roll: 'role',
  role: 'role',
  företag: 'company',
  company: 'company',
  arbetsgivare: 'company',
  ort: 'place',
  plats: 'place',
  place: 'place',
  nivå: 'level',
  level: 'level',
  status: 'status',
  annonsstatus: 'status',
  varför: 'why',
  why: 'why',
  länk: 'link',
  link: 'link',
  annons: 'link',
};

function JobCard({ text }: { text: string }) {
  const job: Record<string, string> = {};
  for (const line of text.split('\n')) {
    const match = line.match(/^\s*([\p{L} ]+?)\s*:\s*(.+)$/u);
    const key = match && jobKeys[match[1].toLowerCase()];
    if (match && key && !job[key]) {
      job[key] = match[2].trim();
    }
  }
  const link = job.link?.match(/https?:\/\/\S+/)?.[0].replace(/[)>.,]+$/, '');
  return (
    <figure className="job-card">
      <figcaption>
        <b>{job.role ?? 'Job'}</b>
        {job.company && <span>{job.company}</span>}
      </figcaption>
      {(job.place || job.level) && (
        <div className="job-tags">
          {job.place && <span>{job.place}</span>}
          {job.level && <span>{job.level}</span>}
        </div>
      )}
      {job.why && <p className="job-why">{job.why}</p>}
      {(job.status || link) && (
        <div className="job-foot">
          {job.status && <span className="job-status">{job.status}</span>}
          {link && (
            <a className="job-link" href={link} target="_blank" rel="noopener noreferrer">
              Open the ad
              <Icon name="arrowRight" size={14} />
            </a>
          )}
        </div>
      )}
    </figure>
  );
}

// A suggestion from the day planner, written as "Key: value" lines in an activity block.
const activityKeys: Record<string, string> = {
  vad: 'title',
  aktivitet: 'title',
  title: 'title',
  what: 'title',
  plats: 'place',
  place: 'place',
  tid: 'time',
  time: 'time',
  ålder: 'age',
  age: 'age',
  kostnad: 'cost',
  pris: 'cost',
  cost: 'cost',
  price: 'cost',
  varför: 'why',
  why: 'why',
  'bra att veta': 'why',
  länk: 'link',
  link: 'link',
};

function ActivityCard({ text }: { text: string }) {
  const item: Record<string, string> = {};
  for (const line of text.split('\n')) {
    const match = line.match(/^\s*([\p{L} ]+?)\s*:\s*(.+)$/u);
    const key = match && activityKeys[match[1].toLowerCase()];
    if (match && key && !item[key]) {
      item[key] = match[2].trim();
    }
  }
  const link = item.link?.match(/https?:\/\/\S+/)?.[0].replace(/[)>.,]+$/, '');
  const tags = [item.time, item.age, item.cost].filter(Boolean);
  return (
    <figure className="job-card">
      <figcaption>
        <b>{item.title ?? 'Suggestion'}</b>
        {item.place && <span>{item.place}</span>}
      </figcaption>
      {tags.length > 0 && (
        <div className="job-tags">
          {tags.map((tag) => (
            <span key={tag}>{tag}</span>
          ))}
        </div>
      )}
      {item.why && <p className="job-why">{item.why}</p>}
      {link && (
        <div className="job-foot">
          <span className="job-status" />
          <a className="job-link" href={link} target="_blank" rel="noopener noreferrer">
            Read more
            <Icon name="arrowRight" size={14} />
          </a>
        </div>
      )}
    </figure>
  );
}

export function CodeBlock({ lang, code }: { lang: string; code: string }) {
  if (lang.toLowerCase() === 'activity') {
    return <ActivityCard text={code} />;
  }
  if (draftLanguages.has(lang.toLowerCase())) {
    return <DraftCard text={code} />;
  }
  if (lang.toLowerCase() === 'job') {
    return <JobCard text={code} />;
  }
  return (
    <figure className="code-block">
      <figcaption className="block-head">
        <span className="block-label">{languageName(lang)}</span>
        <CopyButton text={code} label="Copy code" showLabel />
      </figcaption>
      <pre>
        <code>{highlight(code, lang)}</code>
      </pre>
    </figure>
  );
}
