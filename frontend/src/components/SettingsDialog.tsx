import { useEffect, useState } from 'react';
import { api, send, type Profile, type Settings, errorText } from '../api';
import { Dialog } from './Dialog';
import { Field } from './Field';
import {
  FoldersSection,
  JobRadarSection,
  MemorySection,
  SiriSection,
  TrustedSitesSection,
} from './SettingsSections';
import { PhoneSection } from './PhoneSection';
import { AccountsSection } from './AccountsSection';
import { ProfilesSection } from './ProfilesSection';
import { SkillsSection } from './Skills';
import { ErrorAlert } from './ErrorAlert';

type NumberKey = { [K in keyof Settings]: Settings[K] extends number ? K : never }[keyof Settings];

const keepAliveOptions = [
  { value: '0', label: 'Unload right after each answer' },
  { value: '5m', label: '5 minutes' },
  { value: '30m', label: '30 minutes' },
  { value: '2h', label: '2 hours' },
  { value: '-1', label: 'Until Ollama stops' },
];

export function SettingsDialog({
  open,
  modelContext,
  local,
  profile,
  onClose,
  onSaved,
}: {
  open: boolean;
  modelContext?: number | null;
  // Folders, profiles and phone pairing can only be managed on the computer itself.
  local: boolean;
  profile: Profile | null;
  onClose: () => void;
  // Lets the app pick up a new default model.
  onSaved?: () => void;
}) {
  // Model settings are shared, so only the owner changes them; everyone has their own instructions.
  const owner = profile?.owner ?? false;
  const [settings, setSettings] = useState<Settings | null>(null);
  // Null until loaded, so a saved default is not called missing while the list is on its way.
  const [models, setModels] = useState<string[] | null>(null);
  const [errors, setErrors] = useState('');
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    if (!open) {
      return;
    }
    setErrors('');
    api<Settings>('/settings')
      .then(setSettings)
      .catch((e) => setErrors(errorText(e)));
    setModels(null);
    api<string[]>('/models')
      .then(setModels)
      .catch(() => setModels([]));
  }, [open]);

  function update<K extends keyof Settings>(key: K, value: Settings[K]) {
    setSettings((previous) => (previous ? { ...previous, [key]: value } : previous));
  }

  function number(key: NumberKey, value: string) {
    update(key, value === '' ? 0 : Number(value));
  }

  async function save() {
    if (!settings) {
      return;
    }
    setSaving(true);
    setErrors('');
    try {
      setSettings(await send<Settings>('/settings', 'PUT', settings));
      onSaved?.();
      onClose();
    } catch (e) {
      setErrors(errorText(e));
    } finally {
      setSaving(false);
    }
  }

  return (
    <Dialog title="Settings" open={open} onClose={onClose}>
      {settings ? (
        <>
          <form
            id="settings-form"
            className="settings-form"
            onSubmit={(e) => {
              e.preventDefault();
              void save();
            }}
          >
            <Field
              id="custom-instructions"
              label="Custom instructions"
              hint="Added to every conversation, for example how you want answers written."
            >
              <textarea
                id="custom-instructions"
                rows={4}
                maxLength={4000}
                aria-describedby="custom-instructions-hint"
                value={settings.customInstructions}
                onChange={(e) => update('customInstructions', e.target.value)}
              />
            </Field>
            {owner && (
              <>
                <fieldset>
                  <legend>Model</legend>
                  <Field
                    id="default-model"
                    label="Default model"
                    hint="Used in chats on every device and by scheduled tasks without a model of their own. Changing it switches every device over."
                  >
                    <select
                      id="default-model"
                      aria-describedby="default-model-hint"
                      value={settings.defaultModel}
                      onChange={(e) => update('defaultModel', e.target.value)}
                    >
                      <option value="">
                        Newest installed{models?.[0] ? ` (${models[0]})` : ''}
                      </option>
                      {settings.defaultModel && !(models ?? []).includes(settings.defaultModel) && (
                        <option value={settings.defaultModel}>
                          {settings.defaultModel}
                          {models ? ' (not installed)' : ''}
                        </option>
                      )}
                      {(models ?? []).map((name) => (
                        <option key={name}>{name}</option>
                      ))}
                    </select>
                  </Field>
                  <div className="field-grid">
                    <Field
                      id="context-window"
                      label="Context window (tokens)"
                      hint={
                        modelContext
                          ? `This model supports up to ${modelContext.toLocaleString()}. Larger windows use more memory.`
                          : 'Larger windows use more memory.'
                      }
                    >
                      <input
                        id="context-window"
                        type="number"
                        min={2048}
                        max={262144}
                        step={1024}
                        aria-describedby="context-window-hint"
                        value={settings.contextWindow}
                        onChange={(e) => number('contextWindow', e.target.value)}
                      />
                    </Field>
                    <Field id="keep-alive" label="Keep model loaded">
                      <select
                        id="keep-alive"
                        value={settings.keepAlive}
                        onChange={(e) => update('keepAlive', e.target.value)}
                      >
                        {!keepAliveOptions.some((o) => o.value === settings.keepAlive) && (
                          <option value={settings.keepAlive}>{settings.keepAlive}</option>
                        )}
                        {keepAliveOptions.map((o) => (
                          <option key={o.value} value={o.value}>
                            {o.label}
                          </option>
                        ))}
                      </select>
                    </Field>
                    <Field
                      id="max-output"
                      label="Answer length (tokens)"
                      hint="Answers that reach this limit are marked and can be continued."
                    >
                      <input
                        id="max-output"
                        type="number"
                        min={256}
                        max={16384}
                        step={256}
                        aria-describedby="max-output-hint"
                        value={settings.maxOutputTokens}
                        onChange={(e) => number('maxOutputTokens', e.target.value)}
                      />
                    </Field>
                    <Field
                      id="thinking-budget"
                      label="Thinking budget (tokens)"
                      hint="Extra room used only when Thinking is on."
                    >
                      <input
                        id="thinking-budget"
                        type="number"
                        min={0}
                        max={32768}
                        step={256}
                        aria-describedby="thinking-budget-hint"
                        value={settings.thinkingTokens}
                        onChange={(e) => number('thinkingTokens', e.target.value)}
                      />
                    </Field>
                  </div>
                </fieldset>
                <fieldset>
                  <legend>Web</legend>
                  <div className="field-grid">
                    <Field id="search-results" label="Search results">
                      <input
                        id="search-results"
                        type="number"
                        min={1}
                        max={20}
                        value={settings.searchResults}
                        onChange={(e) => number('searchResults', e.target.value)}
                      />
                    </Field>
                    <Field
                      id="page-characters"
                      label="Page excerpt (characters)"
                      hint="How much of a page the model sees per read."
                    >
                      <input
                        id="page-characters"
                        type="number"
                        min={1000}
                        max={40000}
                        step={500}
                        aria-describedby="page-characters-hint"
                        value={settings.pageCharacters}
                        onChange={(e) => number('pageCharacters', e.target.value)}
                      />
                    </Field>
                  </div>
                </fieldset>
                <label className="checkbox">
                  <input
                    type="checkbox"
                    checked={settings.autoTitles}
                    onChange={(e) => update('autoTitles', e.target.checked)}
                  />
                  Name new conversations with the model
                </label>
                <label className="checkbox">
                  <input
                    type="checkbox"
                    checked={settings.memoryEnabled}
                    onChange={(e) => update('memoryEnabled', e.target.checked)}
                  />
                  Let Leona remember things between conversations
                </label>
              </>
            )}
          </form>
          {local && <ProfilesSection open={open} current={profile} />}
          {local && owner && <FoldersSection open={open} />}
          <AccountsSection
            open={open}
            canSendSecrets={local || window.location.protocol === 'https:'}
          />
          <MemorySection open={open} />
          <SkillsSection open={open} />
          <TrustedSitesSection open={open} />
          <SiriSection open={open} />
          <JobRadarSection open={open} />
          {local && <PhoneSection open={open} current={profile} />}
          <ErrorAlert error={errors} />
          <div className="dialog-actions">
            <button type="button" className="secondary" onClick={onClose}>
              Cancel
            </button>
            <button className="primary" form="settings-form" disabled={saving}>
              Save
            </button>
          </div>
        </>
      ) : (
        <p className="dialog-loading">{errors || 'Loading…'}</p>
      )}
    </Dialog>
  );
}
