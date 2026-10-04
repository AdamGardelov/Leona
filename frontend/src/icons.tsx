const icons = {
  plus: 'M12 5v14M5 12h14',
  search: 'M11 4a7 7 0 1 0 0 14 7 7 0 0 0 0-14ZM20 20l-3.5-3.5',
  sun: 'M12 8a4 4 0 1 0 0 8 4 4 0 0 0 0-8ZM12 2v2M12 20v2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M2 12h2M20 12h2M4.9 19.1l1.4-1.4M17.7 6.3l1.4-1.4',
  moon: 'M20 14.5A8 8 0 0 1 9.5 4a8 8 0 1 0 10.5 10.5Z',
  refresh: 'M20 11a8 8 0 0 0-14.9-3M4 4v4h4M4 13a8 8 0 0 0 14.9 3M20 20v-4h-4',
  globe: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18ZM3 12h18M12 3a14 14 0 0 1 0 18M12 3a14 14 0 0 0 0 18',
  file: 'M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8ZM14 3v5h5',
  filePlus: 'M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8ZM14 3v5h5M12 11v6M9 14h6',
  folder: 'M3 7a2 2 0 0 1 2-2h4l2 2h8a2 2 0 0 1 2 2v8a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2Z',
  bulb: 'M9 18h6M10 21h4M12 3a6 6 0 0 0-3.5 10.9V16h7v-2.1A6 6 0 0 0 12 3Z',
  send: 'M12 19V5M5 12l7-7 7 7',
  text: 'M4 6h16M4 12h10M4 18h7',
  calendar: 'M4 6h16v14H4ZM4 10h16M8 3v4M16 3v4',
  trash: 'M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7M14 10v7',
  panelLeft: 'M5 4h14a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1ZM9 4v16',
  panelRight: 'M5 4h14a1 1 0 0 1 1 1v14a1 1 0 0 1-1 1H5a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1ZM15 4v16',
  more: 'M12 5.5v.01M12 12v.01M12 18.5v.01',
  pin: 'M9 4h6M10 4v5l-3 4h10l-3-4V4M12 13v7',
  archive: 'M3 5h18v4H3ZM5 9v10h14V9M10 13h4',
  restore: 'M3 12a9 9 0 1 0 3-6.7M3 4v5h5',
  settings:
    'M12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6ZM19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1Z',
  pencil: 'M4 20h4L19 9l-4-4L4 16v4ZM13.5 6.5l4 4',
  copy: 'M9 9h10v11H9ZM5 15V4h11',
  regenerate: 'M20 11a8 8 0 0 0-14.9-3M4 4v4h4M4 13a8 8 0 0 0 14.9 3M20 20v-4h-4',
  check: 'm5 12 5 5 9-10',
  close: 'M6 6l12 12M18 6 6 18',
  alert: 'M12 8v5M12 16.5v.01M12 3l9.5 17h-19Z',
  clock: 'M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18ZM12 7v5l3 2',
  inspect: 'M4 6h16M4 12h16M4 18h9M17 16l2 2 3-4',
  arrowRight: 'M5 12h14M13 6l6 6-6 6',
  back: 'M19 12H5M11 6l-6 6 6 6',
  terminal: 'M4 5h16v14H4ZM7 9l3 3-3 3M12 15h5',
  paperclip:
    'M21 11.5 12.5 20a5 5 0 0 1-7-7L14 4.5a3.3 3.3 0 0 1 4.7 4.7L10.2 17.7a1.7 1.7 0 0 1-2.4-2.4L15.5 7.6',
  camera: 'M4 8h3l2-3h6l2 3h3v11H4ZM12 10a4 4 0 1 0 0 8 4 4 0 0 0 0-8Z',
  image: 'M4 5h16v14H4ZM4 15l4-4 4 4 3-3 5 5M15.5 9h.01',
  compose:
    'M12 4H6a2 2 0 0 0-2 2v12a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-6M18.5 2.5a2.1 2.1 0 0 1 3 3L12 15l-4 1 1-4Z',
  chevronDown: 'm6 9 6 6 6-6',
  arrowDown: 'M12 5v14M5 12l7 7 7-7',
  mail: 'M4 6h16v12H4ZM4 7l8 6 8-6',
  calendarPlus: 'M4 6h16v14H4ZM4 10h16M8 3v4M16 3v4M12 13v5M9.5 15.5h5',
  home: 'M4 11 12 4l8 7M6 9.5V20h12V9.5M10 20v-5h4v5',
  receipt: 'M6 3h12v18l-2-1.5-2 1.5-2-1.5-2 1.5-2-1.5L6 21ZM9 8h6M9 12h6M9 16h3',
  bell: 'M6 16V11a6 6 0 0 1 12 0v5l2 2H4ZM10 20a2 2 0 0 0 4 0',
  eye: 'M2 12s4-7 10-7 10 7 10 7-4 7-10 7S2 12 2 12ZM12 9a3 3 0 1 0 0 6 3 3 0 0 0 0-6Z',
  user: 'M12 4a4 4 0 1 0 0 8 4 4 0 0 0 0-8ZM4 21a8 8 0 0 1 16 0',
  play: 'M7 5l12 7-12 7Z',
  mic: 'M12 3a3 3 0 0 0-3 3v6a3 3 0 0 0 6 0V6a3 3 0 0 0-3-3ZM5 11a7 7 0 0 0 14 0M12 18v3M9 21h6',
  speaker: 'M4 9h4l5-4v14l-5-4H4ZM16.5 8.5a5 5 0 0 1 0 7M19 6a8.5 8.5 0 0 1 0 12',
  bookmark: 'M6 4h12v16l-6-4-6 4Z',
  bolt: 'M13 3 5 14h6l-1 7 8-11h-6Z',
  music: 'M9 18V5l12-2v13M9 18a3 3 0 1 1-6 0 3 3 0 0 1 6 0ZM21 16a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z',
  document: 'M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h12a1 1 0 0 0 1-1V8ZM14 3v5h5M8 13h8M8 17h6',
  fileSearch:
    'M14 3H6a1 1 0 0 0-1 1v16a1 1 0 0 0 1 1h5M14 3v5h5v3M16.5 14a2.5 2.5 0 1 0 0 5 2.5 2.5 0 0 0 0-5ZM21 21l-2.7-2.7',
};
export type IconName = keyof typeof icons;

export function Icon({ name, size = 16 }: { name: IconName; size?: number }) {
  // Phones scale icons up through --icon, along with the text.
  const scaled = `calc(${size}px * var(--icon, 1))`;
  return (
    <svg
      width={size}
      height={size}
      style={{ width: scaled, height: scaled }}
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="2"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={icons[name]} />
    </svg>
  );
}

export function Mark({ size = 30 }: { size?: number }) {
  return (
    <svg className="mark" width={size} height={size} viewBox="0 0 24 24" aria-hidden="true">
      <rect width="24" height="24" rx="7" />
      <path d="M8 5.5V12.5A5.5 5.5 0 0 0 13.5 18H17.5" />
      <circle cx="16.25" cy="8.25" r="2.1" />
    </svg>
  );
}

export function PanelToggle({
  side,
  open,
  onToggle,
}: {
  side: 'left' | 'right';
  open: boolean;
  onToggle: () => void;
}) {
  const label = `${open ? 'Hide' : 'Show'} ${side === 'left' ? 'conversations' : 'activity'}`;
  return (
    <button
      className={`panel-toggle ${side}`}
      aria-controls={side === 'left' ? 'sidebar' : 'activity-panel'}
      aria-expanded={open}
      aria-label={label}
      title={label}
      onClick={onToggle}
    >
      <Icon name={side === 'left' ? 'panelLeft' : 'panelRight'} size={18} />
    </button>
  );
}
