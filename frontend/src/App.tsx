import { useEffect, useRef, useState } from 'react';
import {
  api,
  ApiError,
  readStorage,
  send,
  writeStorage,
  type AgentRun,
  type AttachmentRef,
  type Capabilities,
  type Conversation,
  type MailItem,
  type Message,
  type Session,
  type Settings,
  type Source,
  type StepStatus,
  parseArguments,
  errorText,
} from './api';
import { Icon, Mark, PanelToggle, type IconName } from './icons';
import { rememberedRun, useRun } from './useRun';
import { useDrawerGestures } from './drawer';
import { canRecord, canSpeak, speak, speechAvailable, stopSpeaking, unlockSpeech } from './voice';
import { ActivityPanel } from './components/ActivityPanel';
import { AttachSheet, Composer, type Toggle } from './components/Composer';
import { SkillDialog } from './components/Skills';
import { isImage, uploadFile, type PendingAttachment } from './attachments';
import { InspectorDialog } from './components/InspectorDialog';
import { SettingsDialog } from './components/SettingsDialog';
import { AutomationsDialog } from './components/AutomationsDialog';
import { NotificationsDialog } from './components/NotificationsDialog';
import { Sidebar } from './components/Sidebar';
import { Thread } from './components/Thread';

// Matches RunStatus.Active in the backend.
const activeStatuses = ['queued', 'running', 'awaiting_approval'];

const suggestions = [
  {
    icon: 'calendar',
    title: 'Plan my week',
    description: 'from the calendar and unread mail',
    prompt: 'Planera min vecka utifrån kalendern och olästa mejl.',
  },
  {
    icon: 'music',
    title: 'Concerts for me',
    description: 'in Göteborg this autumn',
    prompt: 'Vilka konserter i Göteborg i höst passar min musiksmak?',
  },
  {
    icon: 'home',
    title: 'Home',
    description: 'lights, heating and sensors by room',
    prompt: 'Hur ser det ut hemma? Lampor och temperatur per rum.',
  },
] as const;

type Queued = {
  key: number;
  conversationId: number | null;
  text: string;
  attachments: AttachmentRef[];
};

// The tool switches a message is sent with; the keys are those of the run input.
type Tools = { web: boolean; files: boolean; commands: boolean; accounts: boolean; think: boolean };

function toolsOf(source: Partial<Tools>): Tools {
  return {
    web: !!source.web,
    files: !!source.files,
    commands: !!source.commands,
    accounts: !!source.accounts,
    think: !!source.think,
  };
}

// The switches by the message box, in order. File and terminal tools work on the computer owner's
// files, so other profiles do not get them.
const toolSwitches: {
  key: keyof Tools;
  label: string;
  icon: IconName;
  hint: string;
  needs: 'tools' | 'thinking';
  ownerOnly?: boolean;
}[] = [
  {
    key: 'web',
    label: 'Web',
    icon: 'globe',
    needs: 'tools',
    hint: 'Search and read public webpages',
  },
  {
    key: 'files',
    label: 'Files',
    icon: 'file',
    needs: 'tools',
    ownerOnly: true,
    hint: 'Search, read and edit files in the workspace and your added folders; changes need approval',
  },
  {
    key: 'commands',
    label: 'Terminal',
    icon: 'terminal',
    needs: 'tools',
    ownerOnly: true,
    hint: 'Let Leona propose shell commands; each one needs your approval',
  },
  {
    key: 'accounts',
    label: 'Personal',
    icon: 'user',
    needs: 'tools',
    hint: 'Mail, calendars, home, expenses and schedules; sending and changes need your approval',
  },
  {
    key: 'think',
    label: 'Thinking',
    icon: 'bulb',
    needs: 'thinking',
    hint: 'Let the model reason before answering',
  },
];
type HistoryMessage = Omit<Message, 'tools'> & {
  tools?: {
    name: string;
    arguments: string;
    status: string;
    sources?: Source[];
    drafts?: string[];
    mails?: MailItem[];
  }[];
};

// Saved tool steps arrive with their arguments as a JSON excerpt.
function fromHistory(history: HistoryMessage[]): Message[] {
  return history.map((m) => ({
    ...m,
    tools: m.tools?.map((t) => ({
      name: t.name,
      arguments: parseArguments(t.arguments),
      status: t.status as StepStatus,
      sources: t.sources,
      drafts: t.drafts,
      mails: t.mails?.length ? t.mails : undefined,
    })),
  }));
}

export function App({ session }: { session: Session }) {
  const owner = session.profile?.owner ?? false;
  const [theme, setTheme] = useState<'dark' | 'light'>(() => {
    try {
      return localStorage.getItem('leona-theme') === 'light' ? 'light' : 'dark';
    } catch {
      return 'dark';
    }
  });
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    try {
      localStorage.setItem('leona-theme', theme);
    } catch {
      // The switch still works if browser storage is unavailable.
    }
  }, [theme]);
  const [panels, setPanels] = useState(() =>
    // The activity panel starts hidden in the minimal layout; v2 resets earlier choices once.
    readStorage('leona-panels-v2', { left: true, right: false }),
  );
  useEffect(() => writeStorage('leona-panels-v2', panels), [panels]);
  const toggleLeft = () => setPanels((p) => ({ ...p, left: !p.left }));
  const toggleRight = () => setPanels((p) => ({ ...p, right: !p.right }));

  const [preferences] = useState(() =>
    readStorage<Tools & { model: string }>('leona-composer', {
      model: '',
      ...toolsOf({ web: true }),
    }),
  );
  const [conversations, setConversations] = useState<Conversation[]>([]);
  const [archived, setArchived] = useState<Conversation[]>([]);
  const [showArchived, setShowArchived] = useState(false);
  const [id, setId] = useState<number | null>(null);
  const [messages, setMessages] = useState<Message[]>([]);
  const [models, setModels] = useState<string[]>([]);
  const [model, setModel] = useState(preferences.model);
  const [capabilities, setCapabilities] = useState<Capabilities | null>(null);
  const [tools, setTools] = useState(() => toolsOf(preferences));
  const [text, setText] = useState('');
  const [attachments, setAttachments] = useState<PendingAttachment[]>([]);
  const [sheet, setSheet] = useState(false);
  const [deleting, setDeleting] = useState(false);
  const [error, setError] = useState('');
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [inspectorOpen, setInspectorOpen] = useState(false);
  const [automationsOpen, setAutomationsOpen] = useState(false);
  const [skillOpen, setSkillOpen] = useState(false);
  const [notificationsOpen, setNotificationsOpen] = useState(false);
  const [unread, setUnread] = useState(0);
  // On phones the conversation list is a drawer over the chat.
  const [drawer, setDrawer] = useState(false);
  const selectionVersion = useRef(0);
  // The chat follows a growing answer only while you are at the bottom; scroll up to read in peace.
  const chat = useRef<HTMLElement>(null);
  const [atBottom, setAtBottom] = useState(true);
  // Messages written while Leona answers wait here and are sent, in order, when she is done.
  const [queue, setQueue] = useState<Queued[]>([]);
  const stoppedByUser = useRef(false);

  const run = useRun({
    setMessages,
    setError,
    onStart: (started: AgentRun) => {
      setId(started.conversationId);
      setModel(started.input.model);
      setTools(toolsOf(started.input));
    },
    onFinished: (_, saved) => {
      if (saved) {
        setMessages(fromHistory(saved as HistoryMessage[]));
      }
      void refresh().catch(() => {});
      void loadUnread();
    },
    onTitle: () => void refresh().catch(() => {}),
  });
  const supportsTools = capabilities?.tools ?? false;
  const supportsThinking = capabilities?.thinking ?? false;

  useEffect(() => writeStorage('leona-composer', { model, ...tools }), [model, tools]);
  // A switch only counts when the model supports it and the profile may use it.
  const allowed = (t: (typeof toolSwitches)[number]) =>
    (t.needs === 'thinking' ? supportsThinking : supportsTools) && (owner || !t.ownerOnly);

  async function refresh() {
    const [active, stored] = await Promise.all([
      api<Conversation[]>('/conversations'),
      api<Conversation[]>('/conversations?archived=true'),
    ]);
    setConversations(active);
    setArchived(stored);
  }

  // A chat counts as read once it has been open on screen; the dot then goes away on every device.
  function markRead(conversationId: number) {
    setConversations((list) =>
      list.map((c) => (c.id === conversationId ? { ...c, unread: false } : c)),
    );
    void send(`/conversations/${conversationId}/read`, 'POST').catch(() => {});
  }

  const unreadChats = conversations.filter((c) => c.unread).length;

  // The count also shows on the app icon (home screen apps on iPhone and installed apps elsewhere).
  useEffect(() => {
    const badge = navigator as Navigator & {
      setAppBadge?: (count: number) => Promise<void>;
      clearAppBadge?: () => Promise<void>;
    };
    if (unreadChats > 0) {
      void badge.setAppBadge?.(unreadChats).catch(() => {});
    } else {
      void badge.clearAppBadge?.().catch(() => {});
    }
  }, [unreadChats]);

  // Replies and notifications from schedules, Siri or the other device arrive without this page asking;
  // look again when the app comes back into view, and every minute while it is on screen.
  useEffect(() => {
    function look() {
      if (document.visibilityState === 'visible') {
        void refresh().catch(() => {});
        void loadUnread();
      }
    }
    document.addEventListener('visibilitychange', look);
    const timer = window.setInterval(look, 60_000);
    return () => {
      document.removeEventListener('visibilitychange', look);
      window.clearInterval(timer);
    };
  }, []);

  async function loadUnread() {
    const data = await api<{ unread: number }>('/notifications').catch(() => null);
    if (data) {
      setUnread(data.unread);
    }
  }

  // The default model from Settings takes over on every device each time it changes; a model picked
  // here afterwards stays until the default changes again.
  async function loadModels() {
    const [names, settings] = await Promise.all([
      api<string[]>('/models'),
      api<Settings>('/settings').catch(() => null),
    ]);
    setModels(names);
    const preferred =
      settings?.defaultModel && names.includes(settings.defaultModel)
        ? settings.defaultModel
        : (names[0] ?? '');
    if (settings && settings.defaultModel !== readStorage('leona-default-model', '')) {
      writeStorage('leona-default-model', settings.defaultModel);
      setModel(preferred);
      return;
    }
    setModel((previous) => (names.includes(previous) ? previous : preferred));
  }

  useEffect(() => {
    let mounted = true;
    async function restore() {
      try {
        await Promise.all([refresh(), loadModels()]);
        void loadUnread();
        const active = await api<AgentRun[]>('/runs/active');
        const remembered = rememberedRun();
        let found: AgentRun | undefined = active.find((r) => r.id === remembered) ?? active[0];
        if (!found && remembered) {
          found = await api<AgentRun>(`/runs/${remembered}`).catch(() => undefined);
        }
        if (!mounted) {
          return;
        }
        // A notification opens /?conversation=ID; that chat wins over a run in another chat.
        const linked = Number(new URLSearchParams(window.location.search).get('conversation'));
        if (linked) {
          window.history.replaceState(null, '', '/');
          pendingLink.current = linked;
        }
        if (found && (!linked || found.conversationId === linked)) {
          await run.watch(found);
        } else {
          run.setBusy(false);
          // Back from connecting Spotify: show the account, or say why it did not work.
          const spotify = new URLSearchParams(window.location.search).get('spotify');
          if (spotify) {
            window.history.replaceState(null, '', '/');
            if (spotify === 'connected') {
              setSettingsOpen(true);
            } else {
              setError(
                spotify === 'denied'
                  ? 'Spotify was not connected: access was declined.'
                  : 'Spotify could not be connected. Check the Client ID and redirect address, then try again.',
              );
            }
          }
        }
      } catch (e) {
        if (mounted) {
          setError(errorText(e));
          run.setBusy(false);
        }
      }
    }
    void restore();
    return () => {
      mounted = false;
    };
  }, []);

  useEffect(() => {
    const element = chat.current;
    if (element && atBottom) {
      element.scrollTop = element.scrollHeight;
    }
  }, [messages]);

  // A different conversation starts at its newest message.
  useEffect(() => toBottom(false), [id]);

  function onChatScroll() {
    const element = chat.current;
    if (!element) {
      return;
    }
    setAtBottom(element.scrollHeight - element.scrollTop - element.clientHeight < 80);
  }

  function toBottom(smooth: boolean) {
    setAtBottom(true);
    chat.current?.scrollTo({
      top: chat.current.scrollHeight,
      behavior: smooth ? 'smooth' : 'auto',
    });
  }

  // Sends now, or queues the message while Leona is still answering.
  // spoken: text from the microphone, added to anything already typed.
  function submit(spoken?: string) {
    const value = spoken ? [text.trim(), spoken].filter(Boolean).join(' ') : text;
    if (!run.busy) {
      void startRun(value, { fromComposer: true, attachments: ready });
      return;
    }
    if (deleting || uploading || (!value.trim() && !ready.length)) {
      return;
    }
    setQueue((items) => [
      ...items,
      { key: Date.now(), conversationId: id, text: value.trim(), attachments: ready },
    ]);
    setText('');
    attachments.forEach((a) => a.previewUrl && URL.revokeObjectURL(a.previewUrl));
    setAttachments([]);
    toBottom(true);
  }

  // Voice: the microphone shows where the computer can turn speech into text and the page may record.
  const [voice, setVoice] = useState<true | string | null>(null);
  useEffect(() => {
    void speechAvailable().then((available) => {
      if (!available) {
        setVoice(null);
      } else if (canRecord()) {
        setVoice(true);
      } else {
        setVoice(
          'The microphone needs a secure address: open Leona through its Tailscale https address.',
        );
      }
    });
  }, []);

  // An answer that finishes while its chat is open on screen has been seen.
  useEffect(() => {
    if (!run.busy && id !== null && document.visibilityState === 'visible') {
      markRead(id);
    }
  }, [run.busy]);

  // A question asked by voice gets its answer read aloud. Asked while Leona is answering, it waits in the
  // queue, so the answer after the current one is read: speakAfter counts the answers left to wait for.
  const speakAfter = useRef(0);
  const [speaking, setSpeaking] = useState<number | null>(null);
  const messagesRef = useRef(messages);
  messagesRef.current = messages;

  function readAloud(index: number) {
    if (speaking === index) {
      stopSpeaking();
      setSpeaking(null);
      return;
    }
    unlockSpeech();
    setSpeaking(index);
    speak(messagesRef.current[index]?.content ?? '', () =>
      setSpeaking((current) => (current === index ? null : current)),
    );
  }

  useEffect(() => {
    if (run.busy || speakAfter.current === 0) {
      return;
    }
    speakAfter.current--;
    if (speakAfter.current > 0) {
      return;
    }
    const index = messagesRef.current.length - 1;
    const reply = messagesRef.current[index];
    if (reply?.role === 'assistant' && reply.content) {
      setSpeaking(index);
      speak(reply.content, () => setSpeaking((current) => (current === index ? null : current)));
    }
  }, [run.busy]);

  function sendQueued(item: Queued) {
    setQueue((items) => items.filter((q) => q.key !== item.key));
    void startRun(item.text, { attachments: item.attachments });
  }

  // When an answer is done, the next queued message for this conversation goes out. After Stop
  // nothing is sent automatically; the queue stays for you to send or remove.
  useEffect(() => {
    if (run.busy || deleting) {
      return;
    }
    if (stoppedByUser.current) {
      stoppedByUser.current = false;
      return;
    }
    const next = queue.find((q) => q.conversationId === id);
    if (next) {
      sendQueued(next);
    }
  }, [run.busy, deleting, id, queue]);

  useDrawerGestures(drawer, setDrawer);

  useEffect(() => {
    setCapabilities(null);
    if (!model) {
      return;
    }
    let current = true;
    api<Capabilities>(`/models/capabilities?model=${encodeURIComponent(model)}`)
      .then((c) => {
        if (current) {
          setCapabilities(c);
        }
      })
      .catch(() => {});
    return () => {
      current = false;
    };
  }, [model]);

  async function select(next: number) {
    if (run.busy || deleting) {
      return;
    }
    const version = ++selectionVersion.current;
    try {
      const [history, runs] = await Promise.all([
        api<HistoryMessage[]>(`/conversations/${next}/messages`),
        api<AgentRun[]>(`/conversations/${next}/runs`),
      ]);
      if (version !== selectionVersion.current) {
        return;
      }
      setError('');
      // A run started elsewhere (a schedule, the other device) may still be working or waiting for
      // approval here; follow it so its progress and approval card show.
      if (runs[0] && activeStatuses.includes(runs[0].status)) {
        await run.watch(runs[0]);
        return;
      }
      setId(next);
      setMessages(fromHistory(history));
      run.reset();
      run.setLastRunId(runs[0]?.id ?? null);
      markRead(next);
    } catch (e) {
      setError(errorText(e));
    }
  }

  // Notifications open the chat they came from. While an answer is being written here (or the app is
  // still starting), the chat opens as soon as that is done.
  const pendingLink = useRef<number | null>(null);

  function openConversation(target: number) {
    setNotificationsOpen(false);
    setAutomationsOpen(false);
    setDrawer(false);
    if (run.busy || deleting) {
      pendingLink.current = target;
      return;
    }
    void select(target);
  }

  useEffect(() => {
    if (!run.busy && !deleting && pendingLink.current !== null) {
      const target = pendingLink.current;
      pendingLink.current = null;
      void select(target);
    }
  }, [run.busy, deleting]);

  function openLink(url: string) {
    const target = new URL(url, window.location.origin);
    if (target.origin !== window.location.origin) {
      window.open(target.href, '_blank', 'noopener');
      return;
    }
    const conversation = Number(target.searchParams.get('conversation'));
    if (conversation) {
      openConversation(conversation);
    }
  }

  // The service worker posts here when a notification is tapped while Leona is already open.
  const openLinkRef = useRef(openLink);
  openLinkRef.current = openLink;
  useEffect(() => {
    if (!('serviceWorker' in navigator)) {
      return;
    }
    const onMessage = (event: MessageEvent) => {
      if (event.data?.type === 'open' && typeof event.data.url === 'string') {
        openLinkRef.current(event.data.url);
      }
    };
    navigator.serviceWorker.addEventListener('message', onMessage);
    return () => navigator.serviceWorker.removeEventListener('message', onMessage);
  }, []);

  function newConversation() {
    selectionVersion.current++;
    setId(null);
    setMessages([]);
    run.reset();
    run.setLastRunId(null);
    setError('');
  }

  async function updateConversation(conversation: Conversation, change: Partial<Conversation>) {
    try {
      await send(`/conversations/${conversation.id}`, 'PATCH', change);
      await refresh();
      if (change.archived === false && showArchived && archived.length <= 1) {
        setShowArchived(false);
      }
    } catch (e) {
      setError(errorText(e));
    }
  }

  async function deleteConversation(conversation: Conversation) {
    if (deleting) {
      return;
    }
    if (
      !window.confirm(
        `Delete “${conversation.title}”? Its messages and saved tool excerpts will be permanently deleted.`,
      )
    ) {
      return;
    }
    selectionVersion.current++;
    setDeleting(true);
    setError('');
    try {
      await send(`/conversations/${conversation.id}`, 'DELETE').catch((e) => {
        // A conversation that is already gone is fine.
        if (!(e instanceof ApiError && e.status === 404)) {
          throw e;
        }
      });
      await refresh();
      if (id === conversation.id) {
        newConversation();
        setText('');
      }
    } catch (e) {
      setError(errorText(e));
    } finally {
      setDeleting(false);
    }
  }

  // Uploads files as soon as they are added, so sending is instant.
  function addFiles(files: File[]) {
    const room = 8 - attachments.length;
    if (files.length > room) {
      setError('Attach at most 8 files per message.');
    }
    for (const file of files.slice(0, Math.max(0, room))) {
      const key = `${Date.now()}-${Math.random()}`;
      setAttachments((previous) => [
        ...previous,
        {
          key,
          name: file.name || 'Pasted image',
          kind: isImage(file) ? 'image' : 'document',
          size: file.size,
          status: 'uploading',
        },
      ]);
      uploadFile(file)
        .then(({ ref, previewUrl }) =>
          setAttachments((previous) =>
            previous.map((a) =>
              a.key === key
                ? { ...a, status: 'ready', ref, previewUrl, name: ref.name, size: ref.size }
                : a,
            ),
          ),
        )
        .catch((e: Error) => {
          setError(`${file.name || 'The file'}: ${e.message}`);
          setAttachments((previous) =>
            previous.map((a) => (a.key === key ? { ...a, status: 'error', error: e.message } : a)),
          );
        });
    }
  }

  function removeAttachment(key: string) {
    setAttachments((previous) => {
      const removed = previous.find((a) => a.key === key);
      if (removed?.previewUrl) {
        URL.revokeObjectURL(removed.previewUrl);
      }
      return previous.filter((a) => a.key !== key);
    });
  }

  // Starts a run; rewinding replaces the given user message and everything after it.
  async function startRun(
    prompt: string,
    options: { rewindFrom?: number; fromComposer?: boolean; attachments?: AttachmentRef[] },
  ) {
    const attached = options.attachments ?? [];
    if ((!prompt.trim() && !attached.length) || !model || run.busy || deleting) {
      return;
    }
    run.setBusy(true);
    setError('');
    toBottom(true);
    let active = id;
    try {
      if (active === null) {
        const conversation = await send<Conversation>('/conversations', 'POST');
        active = conversation.id;
        setId(active);
      }
      const created = await send<AgentRun>('/runs', 'POST', {
        conversationId: active,
        rewindFromMessageId: options.rewindFrom,
        input: {
          text: prompt.trim(),
          model,
          ...Object.fromEntries(toolSwitches.map((t) => [t.key, tools[t.key] && allowed(t)])),
          attachments: attached,
        },
      });
      if (options.fromComposer) {
        setText('');
        attachments.forEach((a) => a.previewUrl && URL.revokeObjectURL(a.previewUrl));
        setAttachments([]);
      }
      await run.watch(created);
      await refresh();
    } catch (e) {
      setError(errorText(e));
      run.setBusy(false);
    }
  }

  function regenerate(reply: Message) {
    const index = messages.indexOf(reply);
    const question = messages
      .slice(0, index)
      .reverse()
      .find((m) => m.role === 'user' && m.id !== undefined);
    if (question) {
      void startRun(question.content, {
        rewindFrom: question.id,
        attachments: question.attachments,
      });
    }
  }

  const title = id
    ? ([...conversations, ...archived].find((c) => c.id === id)?.title ?? 'Conversation')
    : 'New conversation';
  const toggles: Toggle[] = toolSwitches
    .filter((t) => owner || !t.ownerOnly)
    .map((t) => ({
      label: t.label,
      icon: t.icon,
      hint: t.hint,
      on: tools[t.key],
      set: (on: boolean) => setTools((previous) => ({ ...previous, [t.key]: on })),
      supported: t.needs === 'thinking' ? supportsThinking : supportsTools,
    }));
  const currentStatus = run.timeline[run.timeline.length - 1]?.text;
  const ready = attachments.filter((a) => a.status === 'ready' && a.ref).map((a) => a.ref!);
  const uploading = attachments.some((a) => a.status === 'uploading');

  return (
    <div
      className={[
        'shell',
        panels.left ? '' : 'left-collapsed',
        panels.right ? '' : 'right-collapsed',
        drawer ? 'drawer-open' : '',
      ].join(' ')}
    >
      <Sidebar
        profile={session.profile}
        local={session.local}
        conversations={conversations}
        archived={archived}
        showArchived={showArchived}
        selectedId={id}
        busy={run.busy}
        deleting={deleting}
        theme={theme}
        onToggleTheme={() => setTheme(theme === 'dark' ? 'light' : 'dark')}
        onTogglePanel={toggleLeft}
        onShowArchived={setShowArchived}
        onNew={() => {
          setDrawer(false);
          newConversation();
        }}
        onSelect={(next) => {
          setDrawer(false);
          void select(next);
        }}
        onCloseDrawer={() => setDrawer(false)}
        onUpdate={(c, change) => void updateConversation(c, change)}
        onDelete={(c) => void deleteConversation(c)}
        onOpenSettings={() => setSettingsOpen(true)}
        onOpenAutomations={() => {
          setDrawer(false);
          setAutomationsOpen(true);
        }}
      />
      {drawer && <div className="drawer-backdrop" onClick={() => setDrawer(false)} />}
      <main>
        <header>
          <button
            className="mobile-menu"
            aria-label={unreadChats > 0 ? 'Open conversations, some unread' : 'Open conversations'}
            aria-controls="sidebar"
            aria-expanded={drawer}
            onClick={() => setDrawer(true)}
          >
            <Icon name="panelLeft" size={20} />
            {unreadChats > 0 && <span className="unread-dot menu-dot" />}
          </button>
          {!panels.left && (
            <>
              <PanelToggle side="left" open={false} onToggle={toggleLeft} />
              <a className="header-brand" href="/" aria-label="Leona">
                <Mark size={24} />
              </a>
            </>
          )}
          <div className={id === null ? 'title-block untitled' : 'title-block'}>
            <h1>{title}</h1>
            {run.busy && (
              <span className={run.approval ? 'header-status waiting' : 'header-status'}>
                {run.approval ? 'Waiting for you' : (currentStatus ?? 'Working…')}
              </span>
            )}
          </div>
          {id === null && (
            <button className="model-pill" aria-label="Choose model" onClick={() => setSheet(true)}>
              <span>{model || 'No model'}</span>
              <Icon name="chevronDown" size={15} />
            </button>
          )}
          {run.busy && (
            <span className={run.approval ? 'pill waiting' : 'pill running'}>
              {run.approval ? 'Awaiting approval' : 'Running'}
            </span>
          )}
          <label className="model">
            <span>Model</span>
            <select
              aria-label="Model"
              value={model}
              disabled={run.busy || !models.length}
              onChange={(e) => setModel(e.target.value)}
            >
              {!models.length && <option value={model}>No models available</option>}
              {models.map((name) => (
                <option key={name}>{name}</option>
              ))}
            </select>
          </label>
          <button
            className="icon-button refresh-models"
            aria-label="Refresh models"
            title="Refresh models"
            disabled={run.busy || deleting}
            onClick={() =>
              loadModels()
                .then(() => setError(''))
                .catch((e) => setError(e.message))
            }
          >
            <Icon name="refresh" />
          </button>
          <button
            className="bell-button"
            aria-label={unread ? `Notifications, ${unread} unread` : 'Notifications'}
            title="Notifications"
            onClick={() => setNotificationsOpen(true)}
          >
            <Icon name="bell" size={19} />
            {unread > 0 && <span className="badge">{unread > 9 ? '9+' : unread}</span>}
          </button>
          <button
            className="mobile-new"
            aria-label="New chat"
            disabled={run.busy || deleting}
            onClick={newConversation}
          >
            <Icon name="compose" size={20} />
          </button>
          {!panels.right && <PanelToggle side="right" open={false} onToggle={toggleRight} />}
        </header>
        <section className="chat" aria-live="polite" ref={chat} onScroll={onChatScroll}>
          {messages.length === 0 ? (
            <div className="welcome">
              <h2>
                {session.profile
                  ? `What’s on your mind, ${session.profile.name}?`
                  : 'What’s on your mind?'}
              </h2>
              <div className="suggestions">
                {suggestions.map((s) => (
                  <button key={s.title} onClick={() => setText(s.prompt)}>
                    <Icon name={s.icon} size={18} />
                    <strong>{s.title}</strong>
                    <span>{s.description}</span>
                  </button>
                ))}
              </div>
            </div>
          ) : (
            <Thread
              messages={messages}
              busy={run.busy}
              currentStatus={currentStatus}
              onEdit={(m, edited) =>
                void startRun(edited, { rewindFrom: m.id, attachments: m.attachments })
              }
              onRegenerate={regenerate}
              onContinue={() =>
                void startRun('Continue exactly where your previous answer stopped.', {})
              }
              onInspect={run.lastRunId ? () => setInspectorOpen(true) : undefined}
              onSaveSkill={models.length > 0 ? () => setSkillOpen(true) : undefined}
              onSpeak={canSpeak() ? readAloud : undefined}
              speaking={speaking}
            />
          )}
        </section>
        <div className="jump-anchor">
          {!atBottom && messages.length > 0 && (
            <button
              type="button"
              className="jump"
              aria-label="Scroll to the newest message"
              title="Newest message"
              onClick={() => toBottom(true)}
            >
              <Icon name="arrowDown" size={18} />
            </button>
          )}
        </div>
        <AttachSheet
          open={sheet}
          busy={run.busy}
          toggles={toggles}
          models={models}
          model={model}
          setModel={setModel}
          onFiles={addFiles}
          onClose={() => setSheet(false)}
        />
        <Composer
          text={text}
          setText={setText}
          busy={run.busy}
          disabled={deleting}
          canSend={
            !deleting && !uploading && (!!text.trim() || ready.length > 0) && models.length > 0
          }
          canStop={!!run.runId}
          toggles={toggles}
          attachments={attachments}
          onFiles={addFiles}
          onRemoveAttachment={removeAttachment}
          onOpenSheet={() => setSheet(true)}
          approval={run.approval}
          deciding={run.deciding}
          error={error}
          onSend={() => submit()}
          voice={voice}
          onVoice={(spoken) => {
            speakAfter.current = run.busy ? 2 : 1;
            submit(spoken);
          }}
          onStop={() => {
            stoppedByUser.current = true;
            void run.stop();
          }}
          queued={queue.filter((q) => q.conversationId === id)}
          onSendQueued={(item) => {
            const queued = queue.find((q) => q.key === item.key);
            if (queued) {
              sendQueued(queued);
            }
          }}
          onRemoveQueued={(item) => setQueue((items) => items.filter((q) => q.key !== item.key))}
          onDecide={(approve, always) => void run.decide(approve, always)}
        />
      </main>
      <ActivityPanel
        busy={run.busy}
        usage={run.usage}
        timeline={run.timeline}
        supportsTools={supportsTools}
        canInspect={!!(run.runId ?? run.lastRunId)}
        onInspect={() => setInspectorOpen(true)}
        onTogglePanel={toggleRight}
      />
      <SettingsDialog
        open={settingsOpen}
        modelContext={capabilities?.contextLength}
        local={session.local}
        profile={session.profile}
        onClose={() => setSettingsOpen(false)}
        onSaved={() => void loadModels().catch(() => {})}
      />
      <SkillDialog
        open={skillOpen}
        conversationId={id}
        model={model}
        onClose={() => setSkillOpen(false)}
      />
      <AutomationsDialog
        open={automationsOpen}
        onClose={() => setAutomationsOpen(false)}
        onOpenConversation={(conversationId) => {
          openConversation(conversationId);
        }}
      />
      <NotificationsDialog
        open={notificationsOpen}
        onClose={() => setNotificationsOpen(false)}
        onRead={() => setUnread(0)}
        onOpenLink={openLink}
      />
      <InspectorDialog
        runId={run.runId ?? run.lastRunId}
        open={inspectorOpen}
        onClose={() => setInspectorOpen(false)}
      />
    </div>
  );
}
