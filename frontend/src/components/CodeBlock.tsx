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

// The "Key: value" lines of a card block, named by a key table; the first line for a name wins.
function parseFields(text: string, keys: Record<string, string>) {
  const fields: Record<string, string> = {};
  for (const line of text.split('\n')) {
    const match = line.match(/^\s*([\p{L} ]+?)\s*:\s*(.+)$/u);
    const key = match && keys[match[1].toLowerCase()];
    if (match && key && !fields[key]) {
      fields[key] = match[2].trim();
    }
  }
  return fields;
}

// The first address in a field, without punctuation the model put after it.
function firstLink(value?: string) {
  return value?.match(/https?:\/\/\S+/)?.[0].replace(/[)>.,]+$/, '');
}

function FieldCard({
  title,
  subtitle,
  tags,
  why,
  status,
  link,
  linkLabel,
}: {
  title: string;
  subtitle?: string;
  tags: (string | undefined)[];
  why?: string;
  status?: string;
  link?: string;
  linkLabel: string;
}) {
  const shown = tags.filter((tag): tag is string => !!tag);
  return (
    <figure className="job-card">
      <figcaption>
        <b>{title}</b>
        {subtitle && <span>{subtitle}</span>}
      </figcaption>
      {shown.length > 0 && (
        <div className="job-tags">
          {shown.map((tag) => (
            <span key={tag}>{tag}</span>
          ))}
        </div>
      )}
      {why && <p className="job-why">{why}</p>}
      {(status || link) && (
        <div className="job-foot">
          <span className="job-status">{status}</span>
          {link && (
            <a className="job-link" href={link} target="_blank" rel="noopener noreferrer">
              {linkLabel}
              <Icon name="arrowRight" size={14} />
            </a>
          )}
        </div>
      )}
    </figure>
  );
}

function JobCard({ text }: { text: string }) {
  const job = parseFields(text, jobKeys);
  return (
    <FieldCard
      title={job.role ?? 'Job'}
      subtitle={job.company}
      tags={[job.place, job.level]}
      why={job.why}
      status={job.status}
      link={firstLink(job.link)}
      linkLabel="Open the ad"
    />
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
  bild: 'image',
  image: 'image',
};

function hostOf(link: string) {
  try {
    return new URL(link).hostname.replace(/^www\./, '');
  } catch {
    return link;
  }
}

// Laid out like a written suggestion rather than a form: the place's photo, a numbered title, when and
// where, why it suits today, and where it comes from. Photos come through Leona, never straight from the site.
function ActivityCard({ text }: { text: string }) {
  const item = parseFields(text, activityKeys);
  const link = firstLink(item.link);
  const image = firstLink(item.image);
  const [broken, setBroken] = useState(false);
  const facts = [item.time, item.place].filter(Boolean).join(' · ');
  // "Se länken" stands in for a fact the sources did not give; it says nothing as a tag.
  const tags = [item.age, item.cost].filter(
    (tag): tag is string => !!tag && !/^se länken|^see the link/i.test(tag),
  );
  const photo = image && !broken && (
    <img
      src={`/api/images?url=${encodeURIComponent(image)}`}
      alt=""
      loading="lazy"
      onError={() => setBroken(true)}
    />
  );
  return (
    <section className="outing">
      {photo &&
        (link ? (
          <a
            className="outing-photo"
            href={link}
            target="_blank"
            rel="noopener noreferrer"
            tabIndex={-1}
          >
            {photo}
          </a>
        ) : (
          <div className="outing-photo">{photo}</div>
        ))}
      <h4 className="outing-title">{item.title ?? 'Suggestion'}</h4>
      {facts && <p className="outing-facts">{facts}</p>}
      {item.why && <p className="outing-why">{item.why}</p>}
      {(tags.length > 0 || link) && (
        <div className="outing-foot">
          {tags.map((tag) => (
            <span key={tag} className="outing-tag">
              {tag}
            </span>
          ))}
          {link && (
            <a className="source-chip" href={link} target="_blank" rel="noopener noreferrer">
              <Icon name="globe" size={12} />
              {hostOf(link)}
            </a>
          )}
        </div>
      )}
    </section>
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
