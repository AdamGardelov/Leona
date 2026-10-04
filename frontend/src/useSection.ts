import { useEffect, useRef, useState } from 'react';
import { errorText } from './api';

// A settings section that loads when its dialog opens, and runs actions that reload it afterwards.
// Errors from either end up in `error`.
export function useSection(open: boolean, load: () => Promise<void>) {
  const [error, setError] = useState('');
  const latest = useRef(load);
  latest.current = load;

  useEffect(() => {
    if (open) {
      setError('');
      latest.current().catch((e) => setError(errorText(e)));
    }
  }, [open]);

  // Returns whether the action worked, so a form knows to clear its input.
  async function run(action: () => Promise<unknown>) {
    setError('');
    try {
      await action();
      await latest.current();
      return true;
    } catch (e) {
      setError(errorText(e));
      return false;
    }
  }

  return { error, run };
}
