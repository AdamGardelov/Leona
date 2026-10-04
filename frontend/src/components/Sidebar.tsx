import {
  Fragment,
  useEffect,
  useRef,
  useState,
  type CSSProperties,
  type KeyboardEvent,
} from 'react';
import { api, send, type Conversation, type Profile, errorText } from '../api';
import { Icon, Mark, PanelToggle, type IconName } from '../icons';

type RowProps = {
  conversation: Conversation;
  selected: boolean;
  selectDisabled: boolean;
  deleteDisabled: boolean;
  onSelect: () => void;
  onRename: (title: string) => void;
  onPin: () => void;
  onArchive: () => void;
  onDelete: () => void;
};

function ConversationRow({
  conversation,
  selected,
  selectDisabled,
  deleteDisabled,
  onSelect,
  onRename,
  onPin,
  onArchive,
  onDelete,
}: RowProps) {
  const [open, setOpen] = useState(false);
  const [editing, setEditing] = useState(false);
  const [draft, setDraft] = useState('');
  const [position, setPosition] = useState<CSSProperties>({});
  const trigger = useRef<HTMLButtonElement>(null);
  const menu = useRef<HTMLDivElement>(null);
  const field = useRef<HTMLInputElement>(null);
  // Enter and Escape hand focus back to the options button (the row itself is disabled during a run);
  // leaving the field by clicking elsewhere does not.
  const refocus = useRef(false);
  // The field can still report a blur after Enter or Escape has finished the rename.
  const renaming = useRef(false);
  const menuId = `conversation-menu-${conversation.id}`;

  useEffect(() => {
    if (editing) {
      field.current?.focus();
      field.current?.select();
    } else if (refocus.current) {
      refocus.current = false;
      trigger.current?.focus();
    }
  }, [editing]);

  useEffect(() => {
    if (!open) {
      return;
    }
    menu.current?.querySelector<HTMLButtonElement>('[role="menuitem"]:not(:disabled)')?.focus();
    function onPointerDown(event: PointerEvent) {
      const target = event.target as Node;
      if (!menu.current?.contains(target) && !trigger.current?.contains(target)) {
        setOpen(false);
      }
    }
    // Follow the trigger when the list scrolls; close once it leaves the visible list.
    function onMove() {
      const list = trigger.current?.closest('nav')?.getBoundingClientRect();
      const row = trigger.current?.getBoundingClientRect();
      if (!row || (list && (row.bottom < list.top || row.top > list.bottom))) {
        setOpen(false);
      } else {
        place();
      }
    }
    document.addEventListener('pointerdown', onPointerDown);
    document.addEventListener('scroll', onMove, true);
    window.addEventListener('resize', onMove);
    return () => {
      document.removeEventListener('pointerdown', onPointerDown);
      document.removeEventListener('scroll', onMove, true);
      window.removeEventListener('resize', onMove);
    };
  }, [open]);

  // Fixed positioning keeps the menu outside the scrolling list; it opens upwards near the bottom.
  function place() {
    if (!trigger.current) {
      return;
    }
    const rect = trigger.current.getBoundingClientRect();
    const right = window.innerWidth - rect.right;
    setPosition(
      window.innerHeight - rect.bottom < 160
        ? { right, bottom: window.innerHeight - rect.top + 4 }
        : { right, top: rect.bottom + 4 },
    );
  }

  function toggle() {
    if (!open) {
      place();
    }
    setOpen(!open);
  }

  function close() {
    setOpen(false);
    trigger.current?.focus();
  }

  function choose(action: () => void) {
    setOpen(false);
    action();
  }

  function startRename() {
    renaming.current = true;
    setDraft(conversation.title);
    setEditing(true);
  }

  function finishRename(save: boolean) {
    if (!renaming.current) {
      return;
    }
    renaming.current = false;
    const title = draft.trim();
    setEditing(false);
    if (save && title && title !== conversation.title) {
      onRename(title);
    }
  }

  function onFieldKeyDown(event: KeyboardEvent<HTMLInputElement>) {
    if (event.key === 'Enter' || event.key === 'Escape') {
      // Escape would otherwise also close the phone drawer.
      event.preventDefault();
      event.stopPropagation();
      refocus.current = true;
      finishRename(event.key === 'Enter');
    }
  }

  function onMenuKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    // Only visible items take part; the Cancel button exists only in the phone action sheet.
    const items = Array.from(
      menu.current?.querySelectorAll<HTMLButtonElement>('[role="menuitem"]:not(:disabled)') ?? [],
    ).filter((item) => item.getClientRects().length > 0);
    const index = items.indexOf(document.activeElement as HTMLButtonElement);
    if (event.key === 'Escape') {
      event.preventDefault();
      close();
    } else if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      const step = event.key === 'ArrowDown' ? 1 : -1;
      items[(index + step + items.length) % items.length]?.focus();
    } else if (event.key === 'Home' || event.key === 'End') {
      event.preventDefault();
      items[event.key === 'Home' ? 0 : items.length - 1]?.focus();
    } else if (event.key === 'Tab') {
      setOpen(false);
    }
  }

  const items: {
    label: string;
    icon: IconName;
    action: () => void;
    danger?: boolean;
    disabled?: boolean;
  }[] = [{ label: 'Rename', icon: 'pencil', action: startRename }];
  if (conversation.archived) {
    items.push({ label: 'Restore', icon: 'restore', action: onArchive });
  } else {
    items.push(
      { label: conversation.pinned ? 'Unpin' : 'Pin', icon: 'pin', action: onPin },
      { label: 'Archive', icon: 'archive', action: onArchive },
    );
  }
  items.push({
    label: 'Delete',
    icon: 'trash',
    action: onDelete,
    danger: true,
    disabled: deleteDisabled,
  });

  if (editing) {
    return (
      <div className="conversation-row editing">
        <input
          ref={field}
          className="rename-field"
          aria-label="Conversation name"
          value={draft}
          maxLength={60}
          enterKeyHint="done"
          onChange={(event) => setDraft(event.target.value)}
          onKeyDown={onFieldKeyDown}
          onBlur={() => finishRename(true)}
        />
      </div>
    );
  }

  return (
    <div className={open ? 'conversation-row menu-open' : 'conversation-row'}>
      <button
        className={selected ? 'selected' : ''}
        aria-current={selected ? 'page' : undefined}
        disabled={selectDisabled}
        onClick={onSelect}
      >
        {conversation.pinned && (
          <span className="pinned-mark">
            <Icon name="pin" size={13} />
            <span className="visually-hidden">Pinned: </span>
          </span>
        )}
        <span className="conversation-title">{conversation.title}</span>
        {conversation.unread && (
          <span className="unread-dot">
            <span className="visually-hidden">, unread</span>
          </span>
        )}
      </button>
      <button
        ref={trigger}
        className="row-menu-trigger"
        aria-label={`Options for ${conversation.title}`}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? menuId : undefined}
        onClick={toggle}
      >
        <svg width="16" height="16" viewBox="0 0 24 24" aria-hidden="true">
          <circle cx="12" cy="5" r="1.8" fill="currentColor" />
          <circle cx="12" cy="12" r="1.8" fill="currentColor" />
          <circle cx="12" cy="19" r="1.8" fill="currentColor" />
        </svg>
      </button>
      {open && <div className="row-menu-scrim" onClick={close} />}
      {open && (
        <div
          id={menuId}
          ref={menu}
          className="row-menu"
          style={position}
          role="menu"
          aria-label={`Options for ${conversation.title}`}
          onKeyDown={onMenuKeyDown}
        >
          <p className="row-menu-title" role="presentation">
            {conversation.title}
          </p>
          {items.map((item) => (
            <button
              key={item.label}
              role="menuitem"
              className={item.danger ? 'danger' : undefined}
              disabled={item.disabled}
              title={item.disabled ? 'Stop the current run first' : undefined}
              onClick={() => choose(item.action)}
            >
              <Icon name={item.icon} size={15} />
              {item.label}
            </button>
          ))}
          <button role="menuitem" className="row-menu-cancel" onClick={close}>
            Cancel
          </button>
        </div>
      )}
    </div>
  );
}

// Who is using Leona. The computer can switch profiles; a paired phone always uses its own.
function ProfileSwitcher({
  profile,
  local,
  onManage,
}: {
  profile: Profile | null;
  local: boolean;
  onManage: () => void;
}) {
  const [open, setOpen] = useState(false);
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [error, setError] = useState('');
  const name = profile?.name ?? 'Leona';
  const badge = (
    <span className="avatar" aria-hidden="true">
      {name.slice(0, 1).toUpperCase()}
    </span>
  );

  useEffect(() => {
    if (!open) {
      return;
    }
    setError('');
    api<Profile[]>('/profiles')
      .then(setProfiles)
      .catch((e) => setError(errorText(e)));
    const close = (e: globalThis.KeyboardEvent) => {
      if (e.key === 'Escape') {
        setOpen(false);
      }
    };
    window.addEventListener('keydown', close);
    return () => window.removeEventListener('keydown', close);
  }, [open]);

  async function use(next: Profile) {
    if (next.id === profile?.id) {
      setOpen(false);
      return;
    }
    try {
      await send(`/profiles/${next.id}/use`, 'POST');
      window.location.assign('/');
    } catch (e) {
      setError(errorText(e));
    }
  }

  if (!local) {
    return (
      <div className="profile-chip" title="This device is paired for this profile">
        {badge}
        <span className="profile-name">{name}</span>
      </div>
    );
  }

  return (
    <div className="profile-switcher">
      <button
        type="button"
        className="profile-chip"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={`Profile: ${name}. Switch profile`}
        title={name}
        onClick={() => setOpen(!open)}
      >
        {badge}
        <span className="profile-name">{name}</span>
        <Icon name="chevronDown" size={14} />
      </button>
      {open && (
        <>
          <button
            type="button"
            className="profile-scrim"
            aria-hidden="true"
            tabIndex={-1}
            onClick={() => setOpen(false)}
          />
          <div className="profile-menu" role="menu" aria-label="Switch profile">
            {profiles.map((p) => (
              <button
                key={p.id}
                type="button"
                role="menuitemradio"
                aria-checked={p.id === profile?.id}
                onClick={() => void use(p)}
              >
                <span className="avatar" aria-hidden="true">
                  {p.name.slice(0, 1).toUpperCase()}
                </span>
                <span className="profile-name">{p.name}</span>
                {p.id === profile?.id && <Icon name="check" size={15} />}
              </button>
            ))}
            {error && <p className="error-text">{error}</p>}
            <button
              type="button"
              role="menuitem"
              className="profile-manage"
              onClick={() => {
                setOpen(false);
                onManage();
              }}
            >
              <Icon name="settings" size={15} />
              Manage profiles
            </button>
          </div>
        </>
      )}
    </div>
  );
}

// Today, Yesterday, Previous 7 days and Earlier, by the last time each conversation was written in.
function byDay(conversations: Conversation[]) {
  const startOfToday = new Date();
  startOfToday.setHours(0, 0, 0, 0);
  const day = 24 * 60 * 60 * 1000;
  const labels = ['Today', 'Yesterday', 'Previous 7 days', 'Earlier'];
  const groups = labels.map((label) => ({ label, conversations: [] as Conversation[] }));
  for (const c of conversations) {
    const time = c.updatedAt ? new Date(c.updatedAt).getTime() : 0;
    const index =
      time >= startOfToday.getTime()
        ? 0
        : time >= startOfToday.getTime() - day
          ? 1
          : time >= startOfToday.getTime() - 7 * day
            ? 2
            : 3;
    groups[index].conversations.push(c);
  }
  return groups.filter((g) => g.conversations.length > 0);
}

type SidebarProps = {
  profile: Profile | null;
  local: boolean;
  conversations: Conversation[];
  archived: Conversation[];
  showArchived: boolean;
  selectedId: number | null;
  busy: boolean;
  deleting: boolean;
  theme: 'dark' | 'light';
  onToggleTheme: () => void;
  onTogglePanel: () => void;
  onShowArchived: (show: boolean) => void;
  onNew: () => void;
  onSelect: (id: number) => void;
  onUpdate: (conversation: Conversation, change: Partial<Conversation>) => void;
  onDelete: (conversation: Conversation) => void;
  onOpenSettings: () => void;
  onOpenAutomations: () => void;
  onCloseDrawer: () => void;
};

export function Sidebar(props: SidebarProps) {
  const [query, setQuery] = useState('');
  const filter = query.trim().toLowerCase();
  const source = props.showArchived ? props.archived : props.conversations;
  const visible = source.filter((c) => c.title.toLowerCase().includes(filter));
  const pinned = visible.filter((c) => c.pinned);
  const groups = byDay(visible.filter((c) => !c.pinned));

  function row(c: Conversation) {
    return (
      <ConversationRow
        key={c.id}
        conversation={c}
        selected={props.selectedId === c.id}
        selectDisabled={props.busy || props.deleting}
        deleteDisabled={(props.busy && props.selectedId === c.id) || props.deleting}
        onSelect={() => props.onSelect(c.id)}
        onRename={(title) => props.onUpdate(c, { title })}
        onPin={() => props.onUpdate(c, { pinned: !c.pinned })}
        onArchive={() => props.onUpdate(c, { archived: !c.archived })}
        onDelete={() => props.onDelete(c)}
      />
    );
  }

  return (
    <aside id="sidebar">
      <div className="sidebar-head">
        <a className="brand" href="/">
          <Mark />
          <span>Leona</span>
        </a>
        <PanelToggle side="left" open onToggle={props.onTogglePanel} />
        <button
          className="drawer-close"
          aria-label="Close conversations"
          onClick={props.onCloseDrawer}
        >
          <Icon name="close" size={20} />
        </button>
      </div>
      <button className="new" disabled={props.busy || props.deleting} onClick={props.onNew}>
        <Icon name="plus" />
        New conversation
      </button>
      <label className="search">
        <Icon name="search" size={15} />
        <input
          aria-label="Search conversations"
          placeholder="Search"
          value={query}
          onChange={(e) => setQuery(e.target.value)}
        />
      </label>
      <nav aria-label={props.showArchived ? 'Archived conversations' : 'Conversations'}>
        {props.showArchived ? (
          <>
            <button className="list-heading back" onClick={() => props.onShowArchived(false)}>
              <Icon name="back" size={14} />
              Archived
            </button>
            {visible.map(row)}
            {!visible.length && (
              <p className="no-results">
                {filter ? 'No matching conversations' : 'Nothing archived'}
              </p>
            )}
          </>
        ) : (
          <>
            {pinned.length > 0 && (
              <>
                <div className="label list-heading">Pinned</div>
                {pinned.map(row)}
              </>
            )}
            {groups.map((group) => (
              <Fragment key={group.label}>
                <div className="label list-heading">{group.label}</div>
                {group.conversations.map(row)}
              </Fragment>
            ))}
            {filter && !visible.length && <p className="no-results">No matching conversations</p>}
          </>
        )}
      </nav>
      {!props.showArchived && props.archived.length > 0 && (
        <button className="archive-link" onClick={() => props.onShowArchived(true)}>
          <Icon name="archive" size={15} />
          Archived
          <span className="count">{props.archived.length}</span>
        </button>
      )}
      <div className="sidebar-footer">
        <ProfileSwitcher
          profile={props.profile}
          local={props.local}
          onManage={props.onOpenSettings}
        />
        <button
          className="icon-button"
          aria-label="Automations"
          title="Automations"
          onClick={props.onOpenAutomations}
        >
          <Icon name="clock" />
        </button>
        <button
          className="icon-button"
          aria-label="Settings"
          title="Settings"
          onClick={props.onOpenSettings}
        >
          <Icon name="settings" />
        </button>
        <button
          className="icon-button"
          aria-label={props.theme === 'dark' ? 'Switch to light theme' : 'Switch to dark theme'}
          title={props.theme === 'dark' ? 'Light theme' : 'Dark theme'}
          onClick={props.onToggleTheme}
        >
          <Icon name={props.theme === 'dark' ? 'sun' : 'moon'} />
        </button>
      </div>
    </aside>
  );
}
