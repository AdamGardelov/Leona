export type Conversation = {
  id: number;
  title: string;
  pinned: boolean;
  archived: boolean;
  createdAt?: string;
  updatedAt?: string;
  // Leona replied after the user last had the chat open.
  unread?: boolean;
  projectId?: number | null;
};
// A group of chats with shared instructions and files.
export type Project = {
  id: number;
  name: string;
  instructions: string;
  chats: number;
  files: AttachmentRef[];
};
export type DocumentStatus = {
  model: string;
  installed: boolean;
  working: boolean;
  documents: number;
  passages: number;
  updated?: string | null;
  failed: { name: string; error: string }[];
};
export type Source = { title: string; url: string };
export type StepStatus =
  'running' | 'awaiting_approval' | 'completed' | 'failed' | 'rejected' | 'expired' | 'unavailable';
export type ToolStep = {
  id?: string;
  name: string;
  arguments: Record<string, unknown>;
  status: StepStatus;
  summary?: string;
  sources?: Source[];
  durationMs?: number;
  // E-mail drafts from the tool's result, shown as cards exactly as saved.
  drafts?: string[];
  // E-mails from a search, shown as a list the user can tick and act on.
  mails?: MailItem[];
};
export type MailItem = {
  id: string;
  date: string;
  from: string;
  subject: string;
  unread: boolean;
  newsletter: boolean;
};
export type AttachmentRef = {
  id: string;
  name: string;
  kind: 'image' | 'document';
  mime: string;
  size: number;
};
export type Message = {
  id?: number;
  role: string;
  content: string;
  thinking?: string;
  complete?: boolean;
  truncated?: boolean;
  tools?: ToolStep[];
  attachments?: AttachmentRef[];
  // The model that wrote a reply; missing on older replies.
  model?: string;
};
type RunInput = {
  text: string;
  model: string;
  web: boolean;
  files: boolean;
  think: boolean;
  commands?: boolean;
  accounts?: boolean;
  attachments?: AttachmentRef[] | null;
};
export type AgentRun = {
  id: string;
  conversationId: number;
  baseMessageId: number;
  status: string;
  createdAt?: string;
  input: RunInput;
};
export type Approval = {
  approvalId: string;
  toolName: string;
  arguments: Record<string, unknown>;
  // Built by the backend: a diff for edits, the folder and command line for commands.
  preview?: string;
  expiresAt: string;
};
export type Capabilities = {
  thinking: boolean;
  tools: boolean;
  vision: boolean;
  contextLength?: number | null;
};
export type Settings = {
  customInstructions: string;
  contextWindow: number;
  maxOutputTokens: number;
  thinkingTokens: number;
  keepAlive: string;
  searchResults: number;
  pageCharacters: number;
  autoTitles: boolean;
  memoryEnabled: boolean;
  // Empty means the model Ollama lists first.
  defaultModel: string;
};
export type Folder = { id: number; name: string; path: string };
export type Profile = { id: number; name: string; owner: boolean };
export type SkillInput = { name: string; whenToUse: string; steps: string };
export type TrustedSite = {
  id: number;
  host: string;
  createdAt: string;
};
export type Skill = SkillInput & {
  id: number;
  uses: number;
  lastUsedAt: string | null;
  createdAt: string;
};
export type Session = {
  local: boolean;
  paired: boolean;
  remoteEnabled: boolean;
  profile: Profile | null;
};
export type AccountKind = 'mail' | 'calendar' | 'home' | 'spotify';
export type Account = {
  id: number;
  kind: AccountKind;
  label: string;
  settings: Record<string, string | number>;
  hasSecret: boolean;
};
export type AppNotification = {
  id: number;
  title: string;
  body: string;
  url?: string | null;
  createdAt: string;
  read: boolean;
};
export type ScheduledTask = {
  id: number;
  name: string;
  prompt: string;
  time: string;
  days: number;
  model: string;
  web: boolean;
  files: boolean;
  accounts: boolean;
  enabled: boolean;
  conversationId?: number | null;
  lastRunAt?: string | null;
  schedule: string;
  nextRun?: string | null;
  // Set while a run is going in the task's chat.
  runStatus?: 'queued' | 'running' | 'awaiting_approval' | null;
};
export type Watch = {
  id: number;
  name: string;
  url: string;
  find: string;
  below?: number | null;
  intervalMinutes: number;
  lastValue?: string | null;
  lastCheckedAt?: string | null;
  lastChangedAt?: string | null;
  lastError?: string | null;
  enabled: boolean;
};
export type MemoryItem = { id: number; text: string; createdAt: string };
// Events persisted by the backend; fields depend on the event type.
export type RunEvent = {
  type: string;
  text?: string;
  id?: string;
  name?: string;
  arguments?: Record<string, unknown>;
  status?: string;
  sources?: Source[];
  durationMs?: number;
  round?: number;
  promptTokens?: number;
  evalTokens?: number;
  evalDurationMs?: number;
  doneReason?: string;
  contextWindow?: number;
  estimatedTokens?: number;
  detail?: Record<string, unknown>;
  approvalId?: string;
  toolName?: string;
  preview?: string;
  expiresAt?: string;
  approved?: boolean;
};
export type RunAction = {
  id: string;
  toolName: string;
  arguments: Record<string, unknown>;
  status: string;
  result?: string | null;
};
export type RunLog = { run: AgentRun; events: RunEvent[]; actions: RunAction[] };

export async function api<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch('/api' + path, init);
  if (!response.ok) {
    throw new Error('Cannot connect. Check that the backend and Ollama are running.');
  }
  return response.json();
}

// A failed request, with the backend's own error text when it gave one.
export class ApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
  }
}

// Sends JSON, or a form or recording as it is, and returns the JSON answer.
export async function send<T>(path: string, method: string, body?: unknown): Promise<T> {
  const raw = body instanceof FormData || body instanceof Blob;
  const response = await fetch('/api' + path, {
    method,
    headers: body === undefined || raw ? undefined : { 'Content-Type': 'application/json' },
    body: body === undefined ? undefined : raw ? body : JSON.stringify(body),
  });
  const text = await response.text();
  let data;
  try {
    data = text ? JSON.parse(text) : undefined;
  } catch {
    data = undefined;
  }
  if (!response.ok) {
    const message =
      data?.error ?? (Array.isArray(data?.errors) ? data.errors.join(' ') : undefined);
    throw new ApiError(message ?? `Request failed (${response.status}).`, response.status);
  }
  return data as T;
}

export function errorText(e: unknown) {
  return e instanceof Error ? e.message : String(e);
}

// History stores tool arguments as an excerpt of their JSON; fall back to an empty object.
export function parseArguments(raw: unknown): Record<string, unknown> {
  if (raw && typeof raw === 'object') {
    return raw as Record<string, unknown>;
  }
  if (typeof raw === 'string') {
    try {
      const parsed = JSON.parse(raw);
      return parsed && typeof parsed === 'object' ? parsed : {};
    } catch {
      return {};
    }
  }
  return {};
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

// A stored object is laid over the fallback, so a preference added later still gets its default; other
// values (a model name, a list) are returned as stored.
export function readStorage<T>(key: string, fallback: T): T {
  try {
    const raw = localStorage.getItem(key);
    if (raw === null) {
      return fallback;
    }
    const stored: unknown = JSON.parse(raw);
    return (isRecord(fallback) && isRecord(stored) ? { ...fallback, ...stored } : stored) as T;
  } catch {
    return fallback;
  }
}

export function writeStorage(key: string, value: unknown) {
  try {
    localStorage.setItem(key, JSON.stringify(value));
  } catch {
    // Browser storage is optional; preferences then last for this session only.
  }
}
