import { useEffect, useState } from 'react';
import { api, send, type Folder, type MemoryItem, type TrustedSite } from '../api';
import { Icon } from '../icons';
import { CopyButton } from './CodeBlock';

type JobEmployer = { id: string; name: string; url: string; system: string };

const systemNames: Record<string, string> = {
  teamtailor: 'Teamtailor',
  lever: 'Lever',
  greenhouse: 'Greenhouse',
  ashby: 'Ashby',
  workable: 'Workable',
  recruitee: 'Recruitee',
  smartrecruiters: 'SmartRecruiters',
  varbi: 'Varbi',
  workday: 'Workday',
  name: 'By name, in Platsbanken',
};

// Employers the job radar watches: their own career pages are read directly, next to Platsbanken.
export function JobRadarSection({ open }: { open: boolean }) {
  const [employers, setEmployers] = useState<JobEmployer[]>([]);
  const [url, setUrl] = useState('');
  const [adding, setAdding] = useState(false);
  const [error, setError] = useState('');

  async function load() {
    setEmployers(await api<JobEmployer[]>('/jobs/employers'));
  }

  useEffect(() => {
    if (open) {
      setError('');
      load().catch((e) => setError(String(e)));
    }
  }, [open]);

  async function add() {
    setError('');
    setAdding(true);
    try {
      await send('/jobs/employers', 'POST', { url });
      setUrl('');
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setAdding(false);
    }
  }

  async function remove(employer: JobEmployer) {
    try {
      await send(`/jobs/employers/${employer.id}`, 'DELETE');
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  return (
    <section className="settings-section" aria-labelledby="jobs-heading">
      <h3 id="jobs-heading">Job radar</h3>
      <p className="hint">
        Employers whose own career pages the job radar reads, next to Platsbanken. Paste the address
        of a career page: Teamtailor, Lever, Greenhouse, Ashby, Workable, Recruitee,
        SmartRecruiters, Varbi and Workday are recognised, also on the company's own domain. Other
        pages, or just a company name, are watched by name: in Platsbanken and in recruiters' ads
        that name them.
      </p>
      {employers.length > 0 && (
        <ul className="settings-list">
          {employers.map((employer) => (
            <li key={employer.id}>
              <span className="list-name">{employer.name}</span>
              <span className="list-path">{systemNames[employer.system] ?? employer.system}</span>
              <button
                type="button"
                className="message-action"
                aria-label={`Stop watching ${employer.name}`}
                title="Remove"
                onClick={() => void remove(employer)}
              >
                <Icon name="trash" size={15} />
              </button>
            </li>
          ))}
        </ul>
      )}
      <form
        className="inline-form"
        onSubmit={(e) => {
          e.preventDefault();
          void add();
        }}
      >
        <label htmlFor="job-employer" className="visually-hidden">
          Career page address or company name
        </label>
        <input
          id="job-employer"
          placeholder="Career page address or company name"
          inputMode="url"
          autoCapitalize="none"
          autoCorrect="off"
          value={url}
          onChange={(e) => setUrl(e.target.value)}
        />
        <button className="secondary" disabled={!url.trim() || adding}>
          {adding ? 'Checking…' : 'Add employer'}
        </button>
      </form>
      {error && (
        <div role="alert" className="error">
          {error}
        </div>
      )}
    </section>
  );
}

type SiriKey = { id: number; name: string; createdAt: string };

// "Hey Siri, ask Leona": a shortcut on the phone dictates the question, sends it with a Siri key and
// reads the answer aloud. A key can only ask questions; it cannot read chats or change settings.
export function SiriSection({ open }: { open: boolean }) {
  const [keys, setKeys] = useState<SiriKey[]>([]);
  const [url, setUrl] = useState<string | null>(null);
  const [created, setCreated] = useState<string | null>(null);
  const [error, setError] = useState('');

  async function load() {
    const data = await api<{ url: string | null; keys: SiriKey[] }>('/siri');
    setKeys(data.keys);
    setUrl(
      data.url ??
        (window.location.protocol === 'https:' ? `${window.location.origin}/api/ask` : null),
    );
  }

  useEffect(() => {
    if (open) {
      setError('');
      setCreated(null);
      load().catch((e) => setError(String(e)));
    }
  }, [open]);

  async function create() {
    setError('');
    try {
      const result = await send<{ key: string }>('/siri/keys', 'POST', { name: 'Siri' });
      setCreated(result.key);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  async function remove(key: SiriKey) {
    if (!window.confirm('Remove this Siri key? The shortcut that uses it stops working.')) {
      return;
    }
    try {
      await send(`/siri/keys/${key.id}`, 'DELETE');
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  return (
    <section className="settings-section" aria-labelledby="siri-heading">
      <h3 id="siri-heading">Siri</h3>
      <p className="hint">
        Say “Hey Siri, Leonafråga” and the answer is read aloud. A shortcut on the iPhone sends the
        question with a Siri key, which can only ask questions. The phone needs Tailscale switched
        on.
      </p>
      {keys.length > 0 && (
        <ul className="settings-list">
          {keys.map((key) => (
            <li key={key.id}>
              <span className="mono list-name">{key.name}</span>
              <span className="list-path">Made {new Date(key.createdAt).toLocaleDateString()}</span>
              <button
                type="button"
                className="message-action"
                aria-label={`Remove the Siri key ${key.name}`}
                title="Remove"
                onClick={() => void remove(key)}
              >
                <Icon name="trash" size={15} />
              </button>
            </li>
          ))}
        </ul>
      )}
      {created ? (
        <div className="siri-setup">
          <p>
            <b>Your Siri key.</b> It is shown only now; copy it into the shortcut.
          </p>
          <div className="siri-value">
            <code>{created}</code>
            <CopyButton text={created} label="Copy the Siri key" showLabel />
          </div>
          {url ? (
            <div className="siri-value">
              <code>{url}</code>
              <CopyButton text={url} label="Copy the address" showLabel />
            </div>
          ) : (
            <p className="hint">
              Set up Tailscale Serve first; the shortcut needs its https address.
            </p>
          )}
          <ol className="siri-steps">
            <li>
              Open <b>Kortkommandon</b> on the iPhone, tap <b>+</b> and name the shortcut{' '}
              <b>Leonafråga</b>. Avoid names such as “Fråga Leona”: Siri takes “Fråga …” as sending
              a message.
            </li>
            <li>
              Add <b>Diktera text</b>. Set the language to Swedish.
            </li>
            <li>
              Add <b>Hämta innehållet i URL</b> with the address above. Open <b>Visa mer</b>: method{' '}
              <b>POST</b>; under <b>Rubriker</b> add <code>Authorization</code> with the value{' '}
              <code>Bearer</code>, a space and the key; under <b>Begärans innehåll</b> choose{' '}
              <b>JSON</b> and add the field <code>text</code> with <b>Dikterad text</b>.
            </li>
            <li>
              Add <b>Läs upp text</b> with <b>Innehållet i URL</b>.
            </li>
            <li>
              Say <b>“Hej Siri, Leonafråga”</b> and ask your question. You can also put the shortcut
              on the Action button or on Back Tap (Tryck på baksidan).
            </li>
          </ol>
          <p className="hint">
            A follow-up within 15 minutes continues the same chat. Answers that need more than about
            45 seconds, or your approval, arrive as a notification.
          </p>
        </div>
      ) : (
        <button type="button" className="secondary" onClick={() => void create()}>
          Create a Siri key
        </button>
      )}
      {error && (
        <div role="alert" className="error">
          {error}
        </div>
      )}
    </section>
  );
}

// Sites Leona may open without asking, even after a chat has read mail or other web pages. Added here
// or with "Always allow" on an approval; removing one makes Leona ask again.
export function TrustedSitesSection({ open }: { open: boolean }) {
  const [sites, setSites] = useState<TrustedSite[]>([]);
  const [address, setAddress] = useState('');
  const [error, setError] = useState('');

  async function load() {
    setSites(await api<TrustedSite[]>('/trusted-sites'));
  }

  useEffect(() => {
    if (open) {
      setError('');
      load().catch((e) => setError(String(e)));
    }
  }, [open]);

  async function add() {
    setError('');
    try {
      await send('/trusted-sites', 'POST', { address });
      setAddress('');
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  async function remove(site: TrustedSite) {
    try {
      await send(`/trusted-sites/${site.id}`, 'DELETE');
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  return (
    <section className="settings-section" aria-labelledby="sites-heading">
      <h3 id="sites-heading">Trusted sites</h3>
      <p className="hint">
        Leona opens pages on these sites without asking, also in scheduled tasks. Subdomains are
        included. Commands, changes and sending still ask every time.
      </p>
      {sites.length > 0 && (
        <ul className="settings-list">
          {sites.map((site) => (
            <li key={site.id}>
              <span className="mono list-name">{site.host}</span>
              <button
                type="button"
                className="message-action"
                aria-label={`Stop trusting ${site.host}`}
                title="Remove"
                onClick={() => void remove(site)}
              >
                <Icon name="trash" size={15} />
              </button>
            </li>
          ))}
        </ul>
      )}
      <form
        className="inline-form"
        onSubmit={(e) => {
          e.preventDefault();
          void add();
        }}
      >
        <label htmlFor="trusted-site" className="visually-hidden">
          Website
        </label>
        <input
          id="trusted-site"
          placeholder="liseberg.se"
          inputMode="url"
          autoCapitalize="none"
          autoCorrect="off"
          value={address}
          onChange={(e) => setAddress(e.target.value)}
        />
        <button className="secondary" disabled={!address.trim()}>
          Add site
        </button>
      </form>
      {error && (
        <div role="alert" className="error">
          {error}
        </div>
      )}
    </section>
  );
}

// Folders are added and removed immediately; only the user can open a folder to the tools.
export function FoldersSection({ open }: { open: boolean }) {
  const [workspace, setWorkspace] = useState('');
  const [folders, setFolders] = useState<Folder[]>([]);
  const [path, setPath] = useState('');
  const [error, setError] = useState('');

  async function load() {
    const data = await api<{ workspace: string; folders: Folder[] }>('/folders');
    setWorkspace(data.workspace);
    setFolders(data.folders);
  }

  useEffect(() => {
    if (open) {
      setError('');
      load().catch((e) => setError(String(e)));
    }
  }, [open]);

  async function add() {
    setError('');
    try {
      await send('/folders', 'POST', { path });
      setPath('');
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  async function remove(folder: Folder) {
    try {
      await send(`/folders/${folder.id}`, 'DELETE');
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  return (
    <section className="settings-section" aria-labelledby="folders-heading">
      <h3 id="folders-heading">Folders</h3>
      <p className="hint">
        File tools and commands work in these folders. Hidden files such as .env and .git are never
        read.
      </p>
      <ul className="settings-list">
        <li>
          <span className="mono list-name">workspace</span>
          <span className="list-path">{workspace}</span>
        </li>
        {folders.map((folder) => (
          <li key={folder.id}>
            <span className="mono list-name">{folder.name}</span>
            <span className="list-path">{folder.path}</span>
            <button
              type="button"
              className="message-action"
              aria-label={`Remove folder ${folder.name}`}
              title="Remove"
              onClick={() => void remove(folder)}
            >
              <Icon name="trash" size={15} />
            </button>
          </li>
        ))}
      </ul>
      <form
        className="inline-form"
        onSubmit={(e) => {
          e.preventDefault();
          void add();
        }}
      >
        <label htmlFor="folder-path" className="visually-hidden">
          Folder path
        </label>
        <input
          id="folder-path"
          placeholder="/home/you/projects/app"
          value={path}
          onChange={(e) => setPath(e.target.value)}
        />
        <button className="secondary" disabled={!path.trim()}>
          Add folder
        </button>
      </form>
      {error && (
        <div role="alert" className="error">
          {error}
        </div>
      )}
    </section>
  );
}

export function MemorySection({ open }: { open: boolean }) {
  const [memories, setMemories] = useState<MemoryItem[]>([]);
  const [query, setQuery] = useState('');
  const [error, setError] = useState('');

  useEffect(() => {
    if (open) {
      setError('');
      api<MemoryItem[]>('/memories')
        .then(setMemories)
        .catch((e) => setError(String(e)));
    }
  }, [open]);

  async function remove(memory: MemoryItem) {
    try {
      await send(`/memories/${memory.id}`, 'DELETE');
      setMemories((previous) => previous.filter((m) => m.id !== memory.id));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  const filter = query.trim().toLowerCase();
  const visible = memories.filter((m) => m.text.toLowerCase().includes(filter));
  return (
    <section className="settings-section" aria-labelledby="memory-heading">
      <h3 id="memory-heading">Memory ({memories.length})</h3>
      {memories.length > 0 && (
        <>
          <label htmlFor="memory-filter" className="visually-hidden">
            Filter memories
          </label>
          <input
            id="memory-filter"
            className="filter-input"
            placeholder="Filter memories"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        </>
      )}
      {visible.length > 0 ? (
        <ul className="settings-list memories">
          {visible.map((memory) => (
            <li key={memory.id}>
              <span className="list-text">{memory.text}</span>
              <span className="list-path">{new Date(memory.createdAt).toLocaleDateString()}</span>
              <button
                type="button"
                className="message-action"
                aria-label={`Forget: ${memory.text}`}
                title="Forget"
                onClick={() => void remove(memory)}
              >
                <Icon name="trash" size={15} />
              </button>
            </li>
          ))}
        </ul>
      ) : (
        <p className="hint">
          {memories.length
            ? 'No memories match.'
            : 'Nothing saved yet. Ask Leona to remember something.'}
        </p>
      )}
      {error && (
        <div role="alert" className="error">
          {error}
        </div>
      )}
    </section>
  );
}
