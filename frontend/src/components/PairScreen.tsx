import { useState } from 'react';
import { send } from '../api';
import { Mark } from '../icons';

// Shown on other devices until they are paired with a code from the computer.
export function PairScreen() {
  const failedLink = new URLSearchParams(window.location.search).get('pairing') === 'failed';
  const [code, setCode] = useState('');
  const [error, setError] = useState(
    failedLink ? 'That pairing link has expired or was already used. Create a new code.' : '',
  );
  const [busy, setBusy] = useState(false);

  async function pair() {
    setBusy(true);
    setError('');
    try {
      await send('/pair', 'POST', { code: code.trim() });
      window.location.replace('/');
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
      setBusy(false);
    }
  }

  return (
    <main className="pair-screen">
      <Mark size={56} />
      <h1>Pair this device</h1>
      <p>
        On your computer, open Leona › Settings › Phone access and create a pairing code. Then enter
        it here, or open the pairing link it shows.
      </p>
      <form
        onSubmit={(e) => {
          e.preventDefault();
          void pair();
        }}
      >
        <label htmlFor="pair-code">Pairing code</label>
        <input
          id="pair-code"
          inputMode="numeric"
          autoComplete="one-time-code"
          pattern="[0-9]{6}"
          maxLength={6}
          placeholder="123456"
          value={code}
          onChange={(e) => setCode(e.target.value.replace(/\D/g, ''))}
        />
        <button className="primary" disabled={busy || code.length !== 6}>
          Pair
        </button>
      </form>
      {error && (
        <div role="alert" className="error">
          {error}
        </div>
      )}
    </main>
  );
}
