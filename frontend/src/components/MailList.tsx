import { useState } from 'react';
import { readStorage, send, writeStorage, type MailItem } from '../api';
import { Icon } from '../icons';

type Action = 'delete' | 'archive' | 'mark_read';

const doneLabels: Record<Action, string> = {
  delete: 'Moved to trash',
  archive: 'Archived',
  mark_read: 'Marked read',
};

// E-mails already moved from a list, so the list still shows it after a reload. Ids stop working once a
// message moves, so old entries are harmless; the newest 500 are kept.
const doneKey = 'leona-mail-done';

function rememberDone(ids: string[], label: string) {
  const done = readStorage<Record<string, string>>(doneKey, {});
  ids.forEach((id) => {
    done[id] = label;
  });
  writeStorage(doneKey, Object.fromEntries(Object.entries(done).slice(-500)));
}

// "Pampers" <pampers@example.com> becomes Pampers; a bare address stays as it is.
function senderName(from: string) {
  const match = from.match(/^"?([^"<]*?)"?\s*<[^>]*>$/);
  return match?.[1].trim() || from.replace(/[<>]/g, '');
}

function shortDate(value: string) {
  const date = new Date(value.replace(' ', 'T'));
  if (Number.isNaN(date.getTime())) {
    return value;
  }
  return date.toDateString() === new Date().toDateString()
    ? date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : date.toLocaleDateString([], { day: 'numeric', month: 'short' });
}

// The result of a mail search as a list to tick. The buttons act directly in the mailbox: pressing one
// is the approval, and deleting only moves mail to the trash.
export function MailList({ items }: { items: MailItem[] }) {
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [done, setDone] = useState<Record<string, string>>(() => {
    const stored = readStorage<Record<string, string>>(doneKey, {});
    return Object.fromEntries(items.filter((i) => stored[i.id]).map((i) => [i.id, stored[i.id]]));
  });
  const [read, setRead] = useState<Set<string>>(new Set());
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState('');
  const [error, setError] = useState('');

  const open = items.filter((i) => !done[i.id]);
  const newsletters = open.filter((i) => i.newsletter);
  const allSelected = open.length > 0 && open.every((i) => selected.has(i.id));

  function toggle(id: string) {
    setSelected((previous) => {
      const next = new Set(previous);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }

  async function act(action: Action) {
    const ids = [...selected];
    setBusy(true);
    setError('');
    setMessage('');
    try {
      const result = await send<{ message: string }>('/mail/manage', 'POST', { ids, action });
      setMessage(result.message);
      if (action === 'mark_read') {
        setRead((previous) => new Set([...previous, ...ids]));
      } else {
        rememberDone(ids, doneLabels[action]);
        setDone((previous) => ({
          ...previous,
          ...Object.fromEntries(ids.map((id) => [id, doneLabels[action]])),
        }));
      }
      setSelected(new Set());
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(false);
    }
  }

  return (
    <figure className="mail-list">
      <figcaption className="block-head">
        <span className="block-label">
          <Icon name="mail" size={14} />
          {items.length === 1 ? '1 e-mail' : `${items.length} e-mails`}
        </span>
        <span className="mail-list-tools">
          {newsletters.length > 0 && (
            <button
              type="button"
              className="link-button"
              onClick={() => setSelected(new Set(newsletters.map((i) => i.id)))}
            >
              Select newsletters
            </button>
          )}
          {open.length > 0 && (
            <button
              type="button"
              className="link-button"
              onClick={() => setSelected(allSelected ? new Set() : new Set(open.map((i) => i.id)))}
            >
              {allSelected ? 'Clear' : 'Select all'}
            </button>
          )}
        </span>
      </figcaption>
      <ul>
        {items.map((item) => {
          const moved = done[item.id];
          const unread = item.unread && !read.has(item.id);
          return (
            <li key={item.id} className={moved ? 'moved' : undefined}>
              <label>
                <input
                  type="checkbox"
                  checked={selected.has(item.id)}
                  disabled={Boolean(moved) || busy}
                  onChange={() => toggle(item.id)}
                />
                <span className="mail-row">
                  <span className="mail-from">
                    {unread && <span className="mail-unread" aria-label="Unread" />}
                    {senderName(item.from)}
                  </span>
                  <span className="mail-date">{shortDate(item.date)}</span>
                  <span className="mail-subject">{item.subject || '(no subject)'}</span>
                  {(moved || item.newsletter) && (
                    <span className="mail-tags">
                      {moved ? (
                        <span className="mail-tag">{moved}</span>
                      ) : (
                        <span className="mail-tag">Newsletter</span>
                      )}
                    </span>
                  )}
                </span>
              </label>
            </li>
          );
        })}
      </ul>
      {selected.size > 0 && (
        <div className="mail-actions">
          <span>{selected.size} selected</span>
          <button
            type="button"
            className="secondary small"
            disabled={busy}
            onClick={() => void act('mark_read')}
          >
            Mark read
          </button>
          <button
            type="button"
            className="secondary small"
            disabled={busy}
            onClick={() => void act('archive')}
          >
            Archive
          </button>
          <button
            type="button"
            className="primary small"
            disabled={busy}
            onClick={() => void act('delete')}
          >
            <Icon name="trash" size={14} />
            Move to trash
          </button>
        </div>
      )}
      {message && (
        <p className="mail-result" role="status">
          {message}
        </p>
      )}
      {error && (
        <p className="mail-result error-text" role="alert">
          {error}
        </p>
      )}
    </figure>
  );
}
