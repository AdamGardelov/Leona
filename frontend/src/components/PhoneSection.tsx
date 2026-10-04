import { useEffect, useState } from 'react';
import { api, send, type Profile } from '../api';
import { Icon } from '../icons';

type Device = { id: number; name: string; createdAt: string; profile: string };
type Status = { enabled: boolean; addresses: string[]; devices: Device[] };
type Pairing = { code: string; expiresAt: string; links: string[] };

// Managed only on the computer: create one-time pairing codes for a profile and remove paired devices.
export function PhoneSection({ open, current }: { open: boolean; current: Profile | null }) {
  const [status, setStatus] = useState<Status | null>(null);
  const [pairing, setPairing] = useState<Pairing | null>(null);
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [profileId, setProfileId] = useState<number | null>(current?.id ?? null);
  const [error, setError] = useState('');

  async function load() {
    const [remote, list] = await Promise.all([api<Status>('/remote'), api<Profile[]>('/profiles')]);
    setStatus(remote);
    setProfiles(list);
  }

  useEffect(() => {
    if (open) {
      setError('');
      setPairing(null);
      load().catch((e) => setError(String(e)));
    }
  }, [open]);

  async function createPairing() {
    setError('');
    try {
      setPairing(await send<Pairing>('/remote/pairings', 'POST', { profileId }));
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  async function remove(device: Device) {
    try {
      await send(`/remote/devices/${device.id}`, 'DELETE');
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  return (
    <section className="settings-section" aria-labelledby="phone-heading">
      <h3 id="phone-heading">Phone access</h3>
      {status && !status.enabled ? (
        <p className="hint">
          Off. Start the backend with <code>Remote:Enabled=true</code> to use Leona from devices on
          your Wi-Fi.
        </p>
      ) : (
        <>
          <p className="hint">
            Open {status?.addresses.join(' or ') || 'this computer’s address'} on your phone and
            pair it with a one-time code. Traffic on the network is not encrypted, so use it on
            Wi-Fi you trust.
          </p>
          {profiles.length > 1 && !pairing && (
            <div className="field">
              <label htmlFor="pair-profile">Pair a phone for</label>
              <select
                id="pair-profile"
                value={profileId ?? ''}
                onChange={(e) => setProfileId(Number(e.target.value))}
              >
                {profiles.map((p) => (
                  <option key={p.id} value={p.id}>
                    {p.name}
                  </option>
                ))}
              </select>
            </div>
          )}
          {pairing ? (
            <div className="pairing">
              <span className="pairing-code mono" aria-label="Pairing code">
                {pairing.code}
              </span>
              <span className="hint">
                Valid once, until{' '}
                {new Date(pairing.expiresAt).toLocaleTimeString([], {
                  hour: '2-digit',
                  minute: '2-digit',
                })}
              </span>
              {pairing.links.map((link) => (
                <a key={link} href={link} className="mono pairing-link">
                  {link}
                </a>
              ))}
            </div>
          ) : (
            <button type="button" className="secondary" onClick={() => void createPairing()}>
              Create pairing code
            </button>
          )}
          {status && status.devices.length > 0 && (
            <ul className="settings-list">
              {status.devices.map((device) => (
                <li key={device.id}>
                  <span className="list-text">{device.name}</span>
                  <span className="list-path">
                    {device.profile} · paired {new Date(device.createdAt).toLocaleDateString()}
                  </span>
                  <button
                    type="button"
                    className="message-action"
                    aria-label={`Remove ${device.name}`}
                    title="Remove device"
                    onClick={() => void remove(device)}
                  >
                    <Icon name="trash" size={15} />
                  </button>
                </li>
              ))}
            </ul>
          )}
        </>
      )}
      {error && (
        <div role="alert" className="error">
          {error}
        </div>
      )}
    </section>
  );
}
