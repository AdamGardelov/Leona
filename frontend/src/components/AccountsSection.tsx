import { useEffect, useState } from 'react';
import { api, send, type Account, type AccountKind, errorText } from '../api';
import { Icon, type IconName } from '../icons';
import { ErrorAlert } from './ErrorAlert';
import { Field } from './Field';

type Draft = {
  id?: number;
  kind: AccountKind;
  label: string;
  preset: string;
  settings: Record<string, string>;
  secret: string;
};

const kinds: { kind: AccountKind; title: string; icon: IconName }[] = [
  { kind: 'mail', title: 'Mail', icon: 'mail' },
  { kind: 'calendar', title: 'Calendar', icon: 'calendar' },
  { kind: 'home', title: 'Home Assistant', icon: 'home' },
  { kind: 'spotify', title: 'Spotify', icon: 'music' },
];

function blank(kind: AccountKind): Draft {
  switch (kind) {
    case 'mail':
      return {
        kind,
        label: '',
        preset: 'loopia',
        secret: '',
        settings: {
          address: '',
          imapHost: 'mailcluster.loopia.se',
          imapPort: '993',
          smtpHost: 'mailcluster.loopia.se',
          smtpPort: '465',
        },
      };
    case 'calendar':
      return {
        kind,
        label: '',
        preset: 'icloud',
        secret: '',
        settings: { url: 'https://caldav.icloud.com', username: '' },
      };
    case 'spotify':
      return { kind, label: 'Spotify', preset: 'spotify', secret: '', settings: { clientId: '' } };
    default:
      return {
        kind,
        label: 'Home',
        preset: 'home',
        secret: '',
        settings: { url: 'http://homeassistant.local:8123' },
      };
  }
}

function summary(account: Account) {
  const s = account.settings;
  if (account.kind === 'mail') {
    return String(s.address ?? '');
  }
  if (account.kind === 'calendar') {
    return String(s.username || s.url || '');
  }
  if (account.kind === 'spotify') {
    return s.user ? `Connected as ${s.user}` : 'Connected';
  }
  return String(s.url ?? '');
}

function TextField(props: {
  id: string;
  label: string;
  value: string;
  type?: string;
  hint?: string;
  placeholder?: string;
  autoComplete?: string;
  onChange: (value: string) => void;
}) {
  return (
    <Field id={props.id} label={props.label} hint={props.hint}>
      <input
        id={props.id}
        type={props.type ?? 'text'}
        value={props.value}
        placeholder={props.placeholder}
        autoComplete={props.autoComplete ?? 'off'}
        aria-describedby={props.hint ? `${props.id}-hint` : undefined}
        onChange={(e) => props.onChange(e.target.value)}
      />
    </Field>
  );
}

// Mail, calendar and Home Assistant accounts. Several of each are fine, for example yours and your partner's.
export function AccountsSection({
  open,
  canSendSecrets,
}: {
  open: boolean;
  canSendSecrets: boolean;
}) {
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [draft, setDraft] = useState<Draft | null>(null);
  const [status, setStatus] = useState<Record<number, string>>({});
  const [error, setError] = useState('');
  const [saving, setSaving] = useState(false);
  const [redirectUris, setRedirectUris] = useState<string[]>([]);

  useEffect(() => {
    if (draft?.kind === 'spotify' && !redirectUris.length) {
      api<{ redirectUris: string[] }>('/spotify/setup')
        .then((setup) => setRedirectUris(setup.redirectUris))
        .catch((e) => setError(errorText(e)));
    }
  }, [draft?.kind]);

  // Spotify signs in on its own page and sends the browser back to Leona.
  async function connectSpotify() {
    if (!draft) {
      return;
    }
    setSaving(true);
    setError('');
    try {
      const { url } = await send<{ url: string }>('/spotify/connect', 'POST', {
        label: draft.label,
        clientId: draft.settings.clientId,
      });
      window.location.assign(url);
    } catch (e) {
      setError(errorText(e));
      setSaving(false);
    }
  }

  async function load() {
    setAccounts(await api<Account[]>('/accounts'));
  }

  useEffect(() => {
    if (open) {
      setDraft(null);
      setError('');
      load().catch((e) => setError(errorText(e)));
    }
  }, [open]);

  function setting(key: string, value: string) {
    setDraft((d) => (d ? { ...d, settings: { ...d.settings, [key]: value } } : d));
  }

  function edit(account: Account) {
    const base = blank(account.kind);
    setDraft({
      ...base,
      id: account.id,
      label: account.label,
      preset: account.kind === 'calendar' && !account.hasSecret ? 'feed' : base.preset,
      settings: Object.fromEntries(
        Object.entries({ ...base.settings, ...account.settings }).map(([k, v]) => [k, String(v)]),
      ),
    });
  }

  async function save() {
    if (!draft) {
      return;
    }
    setSaving(true);
    setError('');
    const settings: Record<string, string | number> = { ...draft.settings };
    for (const key of ['imapPort', 'smtpPort']) {
      if (key in settings) {
        settings[key] = Number(settings[key]);
      }
    }
    try {
      await send(draft.id ? `/accounts/${draft.id}` : '/accounts', draft.id ? 'PUT' : 'POST', {
        kind: draft.kind,
        label: draft.label,
        settings,
        secret: draft.secret || null,
      });
      setDraft(null);
      await load();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setSaving(false);
    }
  }

  async function test(account: Account) {
    setStatus((s) => ({ ...s, [account.id]: 'Testing…' }));
    try {
      const result = await send<{ message: string }>(`/accounts/${account.id}/test`, 'POST');
      setStatus((s) => ({ ...s, [account.id]: result.message }));
    } catch (e) {
      setStatus((s) => ({ ...s, [account.id]: errorText(e) }));
    }
  }

  async function remove(account: Account) {
    if (!window.confirm(`Remove ${account.label}? Leona forgets the saved password.`)) {
      return;
    }
    await send(`/accounts/${account.id}`, 'DELETE').catch((e) => setError(errorText(e)));
    await load();
  }

  return (
    <section className="settings-section" aria-labelledby="accounts-heading">
      <h3 id="accounts-heading">Accounts</h3>
      <p className="hint">
        Used when Personal is on for a message. Reading is free; sending mail, adding events and
        controlling devices always ask you first. Passwords are encrypted on this computer.
      </p>
      {kinds.map(({ kind, title, icon }) => {
        const list = accounts.filter((a) => a.kind === kind);
        return (
          <div key={kind} className="account-group">
            <div className="account-group-head">
              <Icon name={icon} size={16} />
              <span>{title}</span>
              <button
                type="button"
                className="link-button"
                disabled={!canSendSecrets && kind !== 'calendar'}
                onClick={() => setDraft(blank(kind))}
              >
                Add
              </button>
            </div>
            {list.length > 0 && (
              <ul className="settings-list">
                {list.map((account) => (
                  <li key={account.id} className="account-row">
                    <span className="list-text">
                      <b>{account.label}</b>
                      <small>{summary(account)}</small>
                      {status[account.id] && <small role="status">{status[account.id]}</small>}
                    </span>
                    <button
                      type="button"
                      className="secondary small"
                      onClick={() => void test(account)}
                    >
                      Test
                    </button>
                    <button
                      type="button"
                      className="message-action"
                      aria-label={`Edit ${account.label}`}
                      onClick={() => edit(account)}
                    >
                      <Icon name="pencil" size={15} />
                    </button>
                    <button
                      type="button"
                      className="message-action"
                      aria-label={`Remove ${account.label}`}
                      onClick={() => void remove(account)}
                    >
                      <Icon name="trash" size={15} />
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </div>
        );
      })}
      <p className="hint">
        Receipts you ask Leona to record end up in{' '}
        <a href="/api/expenses.csv" download="expenses.csv">
          your expenses spreadsheet (CSV)
        </a>
        .
      </p>
      {!canSendSecrets && (
        <p className="hint">
          Passwords can only be entered on the computer or over HTTPS (Tailscale), not over plain
          Wi-Fi.
        </p>
      )}
      {draft && (
        <form
          className="account-form"
          onSubmit={(e) => {
            e.preventDefault();
            void (draft.kind === 'spotify' ? connectSpotify() : save());
          }}
        >
          <h4>
            {draft.id ? 'Edit' : 'Add'}{' '}
            {/* Product names such as Spotify keep their capital letter. */}
            {draft.kind === 'mail' || draft.kind === 'calendar'
              ? kinds.find((k) => k.kind === draft.kind)?.title.toLowerCase()
              : kinds.find((k) => k.kind === draft.kind)?.title}
          </h4>
          <TextField
            id="account-label"
            label="Name"
            value={draft.label}
            placeholder="For example Adam or Sambo"
            onChange={(label) => setDraft({ ...draft, label })}
          />
          {draft.kind === 'mail' && (
            <>
              <TextField
                id="mail-address"
                label="E-mail address"
                type="email"
                autoComplete="username"
                value={draft.settings.address}
                onChange={(v) => setting('address', v)}
              />
              <TextField
                id="mail-password"
                label="Password"
                type="password"
                autoComplete="new-password"
                value={draft.secret}
                hint={
                  draft.id ? 'Leave empty to keep the saved password.' : 'The mailbox password.'
                }
                onChange={(secret) => setDraft({ ...draft, secret })}
              />
              <details className="account-advanced">
                <summary>Servers (Loopia by default)</summary>
                <div className="field-grid">
                  <TextField
                    id="imap-host"
                    label="IMAP server"
                    value={draft.settings.imapHost}
                    onChange={(v) => setting('imapHost', v)}
                  />
                  <TextField
                    id="imap-port"
                    label="IMAP port"
                    value={draft.settings.imapPort}
                    onChange={(v) => setting('imapPort', v)}
                  />
                  <TextField
                    id="smtp-host"
                    label="SMTP server"
                    value={draft.settings.smtpHost}
                    onChange={(v) => setting('smtpHost', v)}
                  />
                  <TextField
                    id="smtp-port"
                    label="SMTP port"
                    value={draft.settings.smtpPort}
                    onChange={(v) => setting('smtpPort', v)}
                  />
                </div>
              </details>
            </>
          )}
          {draft.kind === 'calendar' && (
            <>
              <div className="field">
                <label htmlFor="calendar-preset">Type</label>
                <select
                  id="calendar-preset"
                  value={draft.preset}
                  onChange={(e) => {
                    const preset = e.target.value;
                    setDraft({
                      ...draft,
                      preset,
                      settings: {
                        url: preset === 'icloud' ? 'https://caldav.icloud.com' : '',
                        username: draft.settings.username,
                      },
                    });
                  }}
                >
                  <option value="icloud">Apple Calendar (iCloud)</option>
                  <option value="feed">Calendar link (.ics, read only)</option>
                  <option value="caldav">Other CalDAV server</option>
                </select>
              </div>
              {draft.preset !== 'icloud' && (
                <TextField
                  id="calendar-url"
                  label={draft.preset === 'feed' ? 'Calendar link' : 'CalDAV address'}
                  value={draft.settings.url}
                  placeholder={
                    draft.preset === 'feed' ? 'webcal://… or https://….ics' : 'https://…'
                  }
                  onChange={(v) => setting('url', v)}
                />
              )}
              {draft.preset !== 'feed' && (
                <>
                  <TextField
                    id="calendar-user"
                    label={draft.preset === 'icloud' ? 'Apple ID' : 'Username'}
                    type="email"
                    autoComplete="username"
                    value={draft.settings.username}
                    onChange={(v) => setting('username', v)}
                  />
                  <TextField
                    id="calendar-password"
                    label="App-specific password"
                    type="password"
                    autoComplete="new-password"
                    value={draft.secret}
                    hint={
                      draft.preset === 'icloud'
                        ? 'Create one at account.apple.com › Sign-In and Security › App-Specific Passwords. Your normal Apple ID password does not work here.'
                        : draft.id
                          ? 'Leave empty to keep the saved password.'
                          : undefined
                    }
                    onChange={(secret) => setDraft({ ...draft, secret })}
                  />
                </>
              )}
            </>
          )}
          {draft.kind === 'spotify' && (
            <>
              <ol className="setup-steps">
                <li>
                  Open{' '}
                  <a
                    href="https://developer.spotify.com/dashboard"
                    target="_blank"
                    rel="noreferrer"
                  >
                    developer.spotify.com/dashboard
                  </a>{' '}
                  and create an app (Spotify Premium is needed). Choose Web API.
                </li>
                <li>
                  Add these redirect URIs exactly:
                  {redirectUris.map((uri) => (
                    <code key={uri} className="setup-uri">
                      {uri}
                    </code>
                  ))}
                </li>
                <li>
                  For a second person, add their Spotify e-mail under User Management in the app.
                </li>
                <li>Copy the app's Client ID here and connect.</li>
              </ol>
              <TextField
                id="spotify-client"
                label="Client ID"
                value={draft.settings.clientId}
                placeholder="32 letters and digits"
                onChange={(v) => setting('clientId', v.trim())}
              />
            </>
          )}
          {draft.kind === 'home' && (
            <>
              <TextField
                id="home-url"
                label="Home Assistant address"
                value={draft.settings.url}
                onChange={(v) => setting('url', v)}
              />
              <TextField
                id="home-token"
                label="Long-lived access token"
                type="password"
                autoComplete="off"
                value={draft.secret}
                hint="Home Assistant › your profile › Security › Long-lived access tokens."
                onChange={(secret) => setDraft({ ...draft, secret })}
              />
            </>
          )}
          <ErrorAlert error={error} />
          <div className="dialog-actions">
            <button type="button" className="secondary" onClick={() => setDraft(null)}>
              Cancel
            </button>
            <button className="primary" disabled={saving}>
              {draft.kind === 'spotify' ? 'Connect Spotify' : 'Save account'}
            </button>
          </div>
        </form>
      )}
      {!draft && <ErrorAlert error={error} />}
    </section>
  );
}
