import { useEffect, useState } from 'react';
import { api, send, type Profile, errorText } from '../api';
import { Icon } from '../icons';
import { ErrorAlert } from './ErrorAlert';

// Managed only on the computer: one profile per person, each with their own chats, accounts and
// notifications. Removing a profile deletes everything in it.
export function ProfilesSection({ open, current }: { open: boolean; current: Profile | null }) {
  const [profiles, setProfiles] = useState<Profile[]>([]);
  const [editing, setEditing] = useState<{ id?: number; name: string } | null>(null);
  const [error, setError] = useState('');

  async function load() {
    setProfiles(await api<Profile[]>('/profiles'));
  }

  useEffect(() => {
    if (open) {
      setEditing(null);
      setError('');
      load().catch((e) => setError(errorText(e)));
    }
  }, [open]);

  async function save() {
    if (!editing) {
      return;
    }
    setError('');
    try {
      await send(
        editing.id ? `/profiles/${editing.id}` : '/profiles',
        editing.id ? 'PUT' : 'POST',
        { name: editing.name },
      );
      setEditing(null);
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  }

  async function remove(profile: Profile) {
    if (
      !window.confirm(
        `Remove ${profile.name}? Their chats, accounts, automations, memories and paired phones are deleted.`,
      )
    ) {
      return;
    }
    setError('');
    try {
      await send(`/profiles/${profile.id}`, 'DELETE');
      await load();
    } catch (e) {
      setError(errorText(e));
    }
  }

  return (
    <section className="settings-section" aria-labelledby="profiles-heading">
      <h3 id="profiles-heading">Profiles</h3>
      <p className="hint">
        Everyone gets their own chats, mail, calendars, automations and notifications. A phone is
        paired for one profile and only sees that profile. Switch profile on this computer from the
        sidebar.
      </p>
      <ul className="settings-list">
        {profiles.map((profile) => (
          <li key={profile.id} className="account-row">
            <span className="avatar" aria-hidden="true">
              {profile.name.slice(0, 1).toUpperCase()}
            </span>
            <span className="list-text">
              <b>{profile.name}</b>
              <small>
                {[
                  profile.owner && 'Owner · files and terminal',
                  profile.id === current?.id && 'In use',
                ]
                  .filter(Boolean)
                  .join(' · ') || 'Chat, web and personal tools'}
              </small>
            </span>
            <button
              type="button"
              className="message-action"
              aria-label={`Rename ${profile.name}`}
              onClick={() => setEditing({ id: profile.id, name: profile.name })}
            >
              <Icon name="pencil" size={15} />
            </button>
            {!profile.owner && (
              <button
                type="button"
                className="message-action"
                aria-label={`Remove ${profile.name}`}
                onClick={() => void remove(profile)}
              >
                <Icon name="trash" size={15} />
              </button>
            )}
          </li>
        ))}
      </ul>
      {editing ? (
        <form
          className="inline-form profile-form"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <input
            aria-label="Profile name"
            value={editing.name}
            maxLength={30}
            placeholder="Name"
            autoFocus
            onChange={(e) => setEditing({ ...editing, name: e.target.value })}
          />
          <button type="button" className="secondary small" onClick={() => setEditing(null)}>
            Cancel
          </button>
          <button className="primary small" disabled={!editing.name.trim()}>
            {editing.id ? 'Rename' : 'Add'}
          </button>
        </form>
      ) : (
        <button type="button" className="secondary" onClick={() => setEditing({ name: '' })}>
          <Icon name="plus" size={15} />
          Add profile
        </button>
      )}
      <ErrorAlert error={error} />
    </section>
  );
}
