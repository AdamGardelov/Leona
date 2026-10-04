import type { IconName } from './icons';
import type { StepStatus } from './api';

function text(value: unknown) {
  return typeof value === 'string' ? value : '';
}

function shortUrl(raw: string) {
  try {
    const url = new URL(raw);
    const path = url.pathname === '/' ? '' : url.pathname;
    return url.host.replace(/^www\./, '') + (path.length > 32 ? path.slice(0, 31) + '…' : path);
  } catch {
    return raw;
  }
}

// Human labels for a tool step, shared by the reply and the activity timeline.
export function describeStep(
  name: string,
  args: Record<string, unknown>,
  status: StepStatus,
): { icon: IconName; title: string; detail: string } {
  const running = status === 'running' || status === 'awaiting_approval';
  switch (name) {
    case 'search_web':
      return {
        icon: 'search',
        title: running ? 'Searching the web' : 'Searched the web',
        detail: text(args.query) ? `“${text(args.query)}”` : '',
      };
    case 'read_page': {
      const find = text(args.find);
      return {
        icon: 'globe',
        title: running ? 'Reading page' : 'Read page',
        detail: shortUrl(text(args.url)) + (find ? ` · “${find}”` : ''),
      };
    }
    case 'list_files':
      return {
        icon: 'folder',
        title: running ? 'Listing files' : 'Listed files',
        detail: text(args.path) || 'workspace',
      };
    case 'read_file':
      return {
        icon: 'file',
        title: running ? 'Reading file' : 'Read file',
        detail: text(args.path),
      };
    case 'create_file':
      return {
        icon: 'filePlus',
        title: status === 'completed' ? 'Created file' : 'Create file',
        detail: text(args.path),
      };
    case 'search_files':
      return {
        icon: 'fileSearch',
        title: running ? 'Searching files' : 'Searched files',
        detail: text(args.query)
          ? `“${text(args.query)}”` + (text(args.glob) ? ` in ${text(args.glob)}` : '')
          : '',
      };
    case 'read_document':
      return {
        icon: 'document',
        title: running ? 'Reading document' : 'Read document',
        detail: text(args.path) + (text(args.find) ? ` · “${text(args.find)}”` : ''),
      };
    case 'edit_file':
      return {
        icon: 'pencil',
        title: status === 'completed' ? 'Edited file' : 'Edit file',
        detail: text(args.path),
      };
    case 'run_command':
      return {
        icon: 'terminal',
        title: status === 'completed' ? 'Ran command' : 'Run command',
        detail: text(args.command),
      };
    case 'mail_search':
      return {
        icon: 'mail',
        title: running ? 'Searching mail' : 'Searched mail',
        detail: text(args.query) ? `“${text(args.query)}”` : text(args.account),
      };
    case 'mail_read':
      return { icon: 'mail', title: running ? 'Reading e-mail' : 'Read e-mail', detail: '' };
    case 'mail_draft':
      return { icon: 'mail', title: 'Saved draft', detail: text(args.subject) };
    case 'mail_send':
      return {
        icon: 'mail',
        title: status === 'completed' ? 'Sent e-mail' : 'Send e-mail',
        detail: text(args.to),
      };
    case 'calendar_events':
      return {
        icon: 'calendar',
        title: running ? 'Checking calendar' : 'Checked calendar',
        detail: [text(args.from), text(args.to)].filter(Boolean).join(' – '),
      };
    case 'calendar_create':
      return {
        icon: 'calendarPlus',
        title: status === 'completed' ? 'Added event' : 'Add event',
        detail: text(args.title),
      };
    case 'home_states':
      return {
        icon: 'home',
        title: running ? 'Checking home' : 'Checked home',
        detail: text(args.query),
      };
    case 'home_action':
      return {
        icon: 'home',
        title: status === 'completed' ? 'Controlled device' : 'Control device',
        detail: `${text(args.service)} ${text(args.entity_id)}`.trim(),
      };
    case 'record_expense':
      return {
        icon: 'receipt',
        title: status === 'completed' ? 'Recorded expense' : 'Record expense',
        detail: text(args.merchant),
      };
    case 'list_expenses':
      return { icon: 'receipt', title: 'Expenses', detail: text(args.month) };
    case 'schedule_task':
      return {
        icon: 'clock',
        title: status === 'completed' ? 'Scheduled' : 'Schedule task',
        detail: text(args.name),
      };
    case 'calendar_update':
      return {
        icon: 'calendar',
        title: status === 'completed' ? 'Changed event' : 'Change event',
        detail: [text(args.title), text(args.date)].filter(Boolean).join(' · '),
      };
    case 'mail_attachment':
      return {
        icon: 'paperclip',
        title: running ? 'Reading attachment' : 'Read attachment',
        detail: text(args.attachment),
      };
    case 'mail_manage': {
      const verbs: Record<string, string> = {
        archive: 'Archived',
        delete: 'Moved to trash',
        flag: 'Flagged',
        unflag: 'Removed flag',
        mark_read: 'Marked read',
        mark_unread: 'Marked unread',
      };
      const count = text(args.ids).split(',').filter(Boolean).length;
      return {
        icon: 'mail',
        title:
          status === 'completed' ? (verbs[text(args.action)] ?? 'Changed e-mail') : 'Change e-mail',
        detail: count === 1 ? '1 e-mail' : `${count} e-mails`,
      };
    }
    case 'calendar_delete':
      return {
        icon: 'trash',
        title: status === 'completed' ? 'Deleted event' : 'Delete event',
        detail: [text(args.title), text(args.date)].filter(Boolean).join(' · '),
      };
    case 'music_taste':
      return {
        icon: 'music',
        title: running ? 'Reading Spotify' : 'Read your music taste',
        detail: '',
      };
    case 'find_concerts':
      return {
        icon: 'music',
        title: running ? 'Looking for concerts' : 'Looked for concerts',
        detail: text(args.artist) || 'Göteborg',
      };
    case 'find_jobs':
      return {
        icon: 'search',
        title: running ? 'Looking for jobs' : 'Looked for jobs',
        detail: text(args.queries),
      };
    case 'find_activities':
      return {
        icon: 'calendar',
        title: running ? 'Looking for things to do' : 'Looked for things to do',
        detail: text(args.date) || 'today',
      };
    case 'weather':
      return {
        icon: 'sun',
        title: running ? 'Checking the weather' : 'Checked the weather',
        detail: text(args.date) || 'today',
      };
    case 'remove_from_photo':
      return {
        icon: 'image',
        title: running ? 'Editing photo' : 'Edited photo',
        detail: text(args.remove) ? `Removing ${text(args.remove)}` : '',
      };
    case 'watch_page':
      return {
        icon: 'eye',
        title: status === 'completed' ? 'Watching page' : 'Watch page',
        detail: shortUrl(text(args.url)),
      };
    case 'save_memory':
      return { icon: 'bookmark', title: 'Saved to memory', detail: text(args.text) };
    case 'save_skill':
      return {
        icon: 'bolt',
        title: status === 'completed' ? 'Saved skill' : 'Save skill',
        detail: text(args.name),
      };
    case 'search_memory':
      return {
        icon: 'bookmark',
        title: running ? 'Searching memory' : 'Searched memory',
        detail: text(args.query) ? `“${text(args.query)}”` : '',
      };
    default:
      return { icon: 'text', title: name, detail: '' };
  }
}

export const statusLabels: Record<StepStatus, string> = {
  running: 'Running',
  awaiting_approval: 'Waiting for you',
  completed: 'Done',
  failed: 'Failed',
  rejected: 'Declined',
  expired: 'Expired',
  unavailable: 'Unavailable',
};
