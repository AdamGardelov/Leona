import { useEffect, useState } from 'react';
import { api, send, type Skill, type SkillInput } from '../api';
import { Icon } from '../icons';
import { Dialog } from './Dialog';

const empty: SkillInput = { name: '', whenToUse: '', steps: '' };

function SkillFields({
  value,
  onChange,
}: {
  value: SkillInput;
  onChange: (value: SkillInput) => void;
}) {
  return (
    <>
      <div className="field">
        <label htmlFor="skill-name">Name</label>
        <input
          id="skill-name"
          maxLength={60}
          value={value.name}
          onChange={(e) => onChange({ ...value, name: e.target.value })}
        />
      </div>
      <div className="field">
        <label htmlFor="skill-when">When to use it</label>
        <input
          id="skill-when"
          maxLength={300}
          aria-describedby="skill-when-hint"
          value={value.whenToUse}
          placeholder="For example: When I ask for my weekly work report"
          onChange={(e) => onChange({ ...value, whenToUse: e.target.value })}
        />
        <p className="hint" id="skill-when-hint">
          Leona uses the skill when a request shares words with its name and this sentence.
        </p>
      </div>
      <div className="field">
        <label htmlFor="skill-steps">Steps</label>
        <textarea
          id="skill-steps"
          rows={7}
          maxLength={4000}
          value={value.steps}
          onChange={(e) => onChange({ ...value, steps: e.target.value })}
        />
      </div>
    </>
  );
}

function complete(value: SkillInput) {
  return !!value.name.trim() && !!value.whenToUse.trim() && !!value.steps.trim();
}

// Opened from an answer: the model drafts a skill from the conversation and you edit it before saving.
export function SkillDialog({
  open,
  conversationId,
  model,
  onClose,
}: {
  open: boolean;
  conversationId: number | null;
  model: string;
  onClose: (saved: boolean) => void;
}) {
  const [value, setValue] = useState<SkillInput>(empty);
  const [drafting, setDrafting] = useState(false);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    if (!open || conversationId === null) {
      return;
    }
    setValue(empty);
    setError('');
    setDrafting(true);
    send<SkillInput>('/skills/draft', 'POST', { conversationId, model })
      .then(setValue)
      .catch((e) => setError(e instanceof Error ? e.message : String(e)))
      .finally(() => setDrafting(false));
  }, [open, conversationId]);

  async function save() {
    setSaving(true);
    setError('');
    try {
      await send('/skills', 'POST', value);
      onClose(true);
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setSaving(false);
    }
  }

  return (
    <Dialog title="Save as skill" open={open} onClose={() => onClose(false)}>
      <p className="hint">
        A skill is a procedure Leona follows the next time you ask for something similar. Keep the
        steps general, not about this one conversation.
      </p>
      {drafting ? (
        <p className="dialog-loading" role="status">
          Leona is writing a first version…
        </p>
      ) : (
        <form
          id="skill-form"
          className="settings-form"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <SkillFields value={value} onChange={setValue} />
        </form>
      )}
      {error && (
        <div role="alert" className="error">
          {error}
        </div>
      )}
      <div className="dialog-actions">
        <button type="button" className="secondary" onClick={() => onClose(false)}>
          Cancel
        </button>
        <button
          className="primary"
          form="skill-form"
          disabled={drafting || saving || !complete(value)}
        >
          Save skill
        </button>
      </div>
    </Dialog>
  );
}

// Settings: the profile's skills with how often each was used.
export function SkillsSection({ open }: { open: boolean }) {
  const [skills, setSkills] = useState<Skill[]>([]);
  const [editing, setEditing] = useState<(SkillInput & { id?: number }) | null>(null);
  const [error, setError] = useState('');

  async function load() {
    setSkills(await api<Skill[]>('/skills'));
  }

  useEffect(() => {
    if (open) {
      setEditing(null);
      setError('');
      load().catch((e) => setError(String(e)));
    }
  }, [open]);

  async function save() {
    if (!editing) {
      return;
    }
    setError('');
    try {
      await send(editing.id ? `/skills/${editing.id}` : '/skills', editing.id ? 'PUT' : 'POST', {
        name: editing.name,
        whenToUse: editing.whenToUse,
        steps: editing.steps,
      });
      setEditing(null);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  async function remove(skill: Skill) {
    if (!window.confirm(`Remove the skill ${skill.name}?`)) {
      return;
    }
    try {
      await send(`/skills/${skill.id}`, 'DELETE');
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    }
  }

  return (
    <section className="settings-section" aria-labelledby="skills-heading">
      <h3 id="skills-heading">Skills ({skills.length})</h3>
      <p className="hint">
        Procedures Leona reuses for similar requests. Save one from an answer with the lightning
        button, or ask Leona to remember how to do something.
      </p>
      {skills.length > 0 && (
        <ul className="settings-list">
          {skills.map((skill) => (
            <li key={skill.id} className="account-row">
              <span className="list-text">
                <b>{skill.name}</b>
                <small>
                  {skill.whenToUse} · used {skill.uses} {skill.uses === 1 ? 'time' : 'times'}
                </small>
              </span>
              <button
                type="button"
                className="message-action"
                aria-label={`Edit ${skill.name}`}
                onClick={() => setEditing({ ...skill })}
              >
                <Icon name="pencil" size={15} />
              </button>
              <button
                type="button"
                className="message-action"
                aria-label={`Remove ${skill.name}`}
                onClick={() => void remove(skill)}
              >
                <Icon name="trash" size={15} />
              </button>
            </li>
          ))}
        </ul>
      )}
      {editing ? (
        <form
          className="account-form"
          onSubmit={(e) => {
            e.preventDefault();
            void save();
          }}
        >
          <h4>{editing.id ? 'Edit skill' : 'New skill'}</h4>
          <SkillFields value={editing} onChange={(value) => setEditing({ ...editing, ...value })} />
          <div className="dialog-actions">
            <button type="button" className="secondary" onClick={() => setEditing(null)}>
              Cancel
            </button>
            <button className="primary" disabled={!complete(editing)}>
              Save skill
            </button>
          </div>
        </form>
      ) : (
        <button type="button" className="secondary" onClick={() => setEditing({ ...empty })}>
          <Icon name="plus" size={15} />
          New skill
        </button>
      )}
      {error && (
        <div role="alert" className="error">
          {error}
        </div>
      )}
    </section>
  );
}
