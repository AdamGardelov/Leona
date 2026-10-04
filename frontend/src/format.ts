// Times and durations as the interface shows them.

// A weekday and time, such as "tis 14:30", for schedules and notifications.
export function weekdayTime(value: string) {
  return new Date(value).toLocaleString([], {
    weekday: 'short',
    hour: '2-digit',
    minute: '2-digit',
  });
}

export function clock(value: string | Date) {
  return new Date(value).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
}

export function seconds(ms: number) {
  return `${(ms / 1000).toFixed(1)} s`;
}
