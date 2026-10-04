import { useEffect, useState } from 'react';
import { api, send, type Folder, type MemoryItem, type TrustedSite } from '../api';
import { Icon } from '../icons';
import { useSection } from '../useSection';
import { CopyButton } from './CodeBlock';
import { ErrorAlert } from './ErrorAlert';

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

function RemoveButton({
  label,
  title = 'Remove',
  onClick,
}: {
  label: string;
  title?: string;
  onClick: () => void;
}) {
  return (
    <button
      type="button"
      className="message-action"
      aria-label={label}
      title={title}
      onClick={onClick}
    >
      <Icon name="trash" size={15} />
    </button>
  );
}

// A one-field form that adds an item to a section's list.
function InlineAdd({
  id,
  label,
  placeholder,
  value,
  onChange,
  onAdd,
  button,
  address = false,
  disabled = false,
}: {
  id: string;
  label: string;
  placeholder: string;
  value: string;
  onChange: (value: string) => void;
  onAdd: () => void;
  button: string;
  // Web addresses get the URL keyboard and no autocorrection.
  address?: boolean;
  disabled?: boolean;
}) {
  return (
    <form
      className="inline-form"
      onSubmit={(e) => {
        e.preventDefault();
        onAdd();
      }}
    >
      <label htmlFor={id} className="visually-hidden">
        {label}
      </label>
      <input
        id={id}
        placeholder={placeholder}
        inputMode={address ? 'url' : undefined}
        autoCapitalize={address ? 'none' : undefined}
        autoCorrect={address ? 'off' : undefined}
        value={value}
        onChange={(e) => onChange(e.target.value)}
      />
      <button className="secondary" disabled={!value.trim() || disabled}>
        {button}
      </button>
    </form>
  );
}

// Employers the job radar watches: their own career pages are read directly, next to Platsbanken.
export function JobRadarSection({ open }: { open: boolean }) {
  const [employers, setEmployers] = useState<JobEmployer[]>([]);
  const [url, setUrl] = useState('');
  const [adding, setAdding] = useState(false);
  const { error, run } = useSection(open, async () => {
    setEmployers(await api<JobEmployer[]>('/jobs/employers'));
  });

  async function add() {
    setAdding(true);
    if (await run(() => send('/jobs/employers', 'POST', { url }))) {
      setUrl('');
    }
    setAdding(false);
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
              <RemoveButton
                label={`Stop watching ${employer.name}`}
                onClick={() => void run(() => send(`/jobs/employers/${employer.id}`, 'DELETE'))}
              />
            </li>
          ))}
        </ul>
      )}
      <InlineAdd
        id="job-employer"
        label="Career page address or company name"
        placeholder="Career page address or company name"
        value={url}
        onChange={setUrl}
        onAdd={() => void add()}
        button={adding ? 'Checking…' : 'Add employer'}
        address
        disabled={adding}
      />
      <ErrorAlert error={error} />
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
  const { error, run } = useSection(open, async () => {
    const data = await api<{ url: string | null; keys: SiriKey[] }>('/siri');
    setKeys(data.keys);
    setUrl(
      data.url ??
        (window.location.protocol === 'https:' ? `${window.location.origin}/api/ask` : null),
    );
  });

  useEffect(() => {
    if (open) {
      setCreated(null);
    }
  }, [open]);

  function create() {
    void run(async () => {
      const result = await send<{ key: string }>('/siri/keys', 'POST', { name: 'Siri' });
      setCreated(result.key);
    });
  }

  function remove(key: SiriKey) {
    if (!window.confirm('Remove this Siri key? The shortcut that uses it stops working.')) {
      return;
    }
    void run(() => send(`/siri/keys/${key.id}`, 'DELETE'));
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
              <RemoveButton label={`Remove the Siri key ${key.name}`} onClick={() => remove(key)} />
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
        <button type="button" className="secondary" onClick={create}>
          Create a Siri key
        </button>
      )}
      <ErrorAlert error={error} />
    </section>
  );
}

// Sites Leona may open without asking, even after a chat has read mail or other web pages. Added here
// or with "Always allow" on an approval; removing one makes Leona ask again.
export function TrustedSitesSection({ open }: { open: boolean }) {
  const [sites, setSites] = useState<TrustedSite[]>([]);
  const [address, setAddress] = useState('');
  const { error, run } = useSection(open, async () => {
    setSites(await api<TrustedSite[]>('/trusted-sites'));
  });

  async function add() {
    if (await run(() => send('/trusted-sites', 'POST', { address }))) {
      setAddress('');
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
              <RemoveButton
                label={`Stop trusting ${site.host}`}
                onClick={() => void run(() => send(`/trusted-sites/${site.id}`, 'DELETE'))}
              />
            </li>
          ))}
        </ul>
      )}
      <InlineAdd
        id="trusted-site"
        label="Website"
        placeholder="liseberg.se"
        value={address}
        onChange={setAddress}
        onAdd={() => void add()}
        button="Add site"
        address
      />
      <ErrorAlert error={error} />
    </section>
  );
}

// Folders are added and removed immediately; only the user can open a folder to the tools.
export function FoldersSection({ open }: { open: boolean }) {
  const [workspace, setWorkspace] = useState('');
  const [folders, setFolders] = useState<Folder[]>([]);
  const [path, setPath] = useState('');
  const { error, run } = useSection(open, async () => {
    const data = await api<{ workspace: string; folders: Folder[] }>('/folders');
    setWorkspace(data.workspace);
    setFolders(data.folders);
  });

  async function add() {
    if (await run(() => send('/folders', 'POST', { path }))) {
      setPath('');
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
            <RemoveButton
              label={`Remove folder ${folder.name}`}
              onClick={() => void run(() => send(`/folders/${folder.id}`, 'DELETE'))}
            />
          </li>
        ))}
      </ul>
      <InlineAdd
        id="folder-path"
        label="Folder path"
        placeholder="/home/you/projects/app"
        value={path}
        onChange={setPath}
        onAdd={() => void add()}
        button="Add folder"
      />
      <ErrorAlert error={error} />
    </section>
  );
}

export function MemorySection({ open }: { open: boolean }) {
  const [memories, setMemories] = useState<MemoryItem[]>([]);
  const [query, setQuery] = useState('');
  const { error, run } = useSection(open, async () => {
    setMemories(await api<MemoryItem[]>('/memories'));
  });

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
              <RemoveButton
                label={`Forget: ${memory.text}`}
                title="Forget"
                onClick={() => void run(() => send(`/memories/${memory.id}`, 'DELETE'))}
              />
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
      <ErrorAlert error={error} />
    </section>
  );
}
