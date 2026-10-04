import { useEffect, useRef, useState } from 'react';
import { errorText, send, type Project } from '../api';
import { describeSize, docBadge, documentTypes, uploadFile } from '../attachments';
import { Icon } from '../icons';
import { Dialog } from './Dialog';
import { ErrorAlert } from './ErrorAlert';
import { Field } from './Field';

// Creates or edits a project: its name, the instructions its chats follow, and the documents they can
// search. A new project gets its files once it exists.
export function ProjectDialog({
  project,
  open,
  onClose,
  onSaved,
  onDeleted,
}: {
  // null creates a new project.
  project: Project | null;
  open: boolean;
  onClose: () => void;
  // The list is read again; close says whether the dialog is done. A new project stays open for files.
  onSaved: (id: number, close: boolean) => void;
  onDeleted: (id: number) => void;
}) {
  const [name, setName] = useState('');
  const [instructions, setInstructions] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const picker = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (open) {
      setName(project?.name ?? '');
      setInstructions(project?.instructions ?? '');
      setError('');
    }
  }, [open, project]);

  async function act(action: () => Promise<void>) {
    setBusy(true);
    setError('');
    try {
      await action();
    } catch (e) {
      setError(errorText(e));
    } finally {
      setBusy(false);
    }
  }

  function save() {
    void act(async () => {
      const saved = await send<Project>(
        project ? `/projects/${project.id}` : '/projects',
        project ? 'PUT' : 'POST',
        {
          name,
          instructions,
        },
      );
      onSaved(saved.id, project !== null);
    });
  }

  function addFiles(files: File[]) {
    if (!project) {
      return;
    }
    void act(async () => {
      for (const file of files) {
        const { ref } = await uploadFile(file);
        await send(`/projects/${project.id}/files`, 'POST', { uploadId: ref.id });
      }
      onSaved(project.id, false);
    });
  }

  function removeFile(id: string) {
    if (!project) {
      return;
    }
    void act(async () => {
      await send(`/projects/${project.id}/files/${id}`, 'DELETE');
      onSaved(project.id, false);
    });
  }

  function remove() {
    if (
      !project ||
      !window.confirm(
        `Delete the project “${project.name}”? Its chats stay; its files are deleted.`,
      )
    ) {
      return;
    }
    void act(async () => {
      await send(`/projects/${project.id}`, 'DELETE');
      onDeleted(project.id);
    });
  }

  return (
    <Dialog title={project ? project.name : 'New project'} open={open} onClose={onClose}>
      <form
        id="project-form"
        className="settings-form"
        onSubmit={(e) => {
          e.preventDefault();
          save();
        }}
      >
        <Field id="project-name" label="Name">
          <input
            id="project-name"
            maxLength={60}
            value={name}
            placeholder="For example: Göra med Walle"
            onChange={(e) => setName(e.target.value)}
          />
        </Field>
        <Field
          id="project-instructions"
          label="Instructions"
          hint="Every chat in the project follows these, for example who it is about and how answers should be."
        >
          <textarea
            id="project-instructions"
            rows={5}
            maxLength={4000}
            aria-describedby="project-instructions-hint"
            value={instructions}
            onChange={(e) => setInstructions(e.target.value)}
          />
        </Field>
      </form>
      {project && (
        <section className="settings-section" aria-labelledby="project-files-heading">
          <h3 id="project-files-heading">Files</h3>
          <p className="hint">
            Documents the project's chats can search: PDF, Word and text files. They stay on this
            computer.
          </p>
          {project.files.length > 0 && (
            <ul className="settings-list">
              {project.files.map((file) => (
                <li key={file.id}>
                  <span className="doc-badge">{docBadge(file.name)}</span>
                  <span className="list-name">{file.name}</span>
                  <span className="list-path">{describeSize(file.size)}</span>
                  <button
                    type="button"
                    className="message-action"
                    aria-label={`Remove ${file.name}`}
                    title="Remove"
                    disabled={busy}
                    onClick={() => removeFile(file.id)}
                  >
                    <Icon name="trash" size={15} />
                  </button>
                </li>
              ))}
            </ul>
          )}
          <button
            type="button"
            className="secondary"
            disabled={busy}
            onClick={() => picker.current?.click()}
          >
            <Icon name="paperclip" size={15} />
            {busy ? 'Working…' : 'Add files'}
          </button>
          <input
            ref={picker}
            type="file"
            accept={documentTypes}
            multiple
            hidden
            onChange={(e) => {
              const files = Array.from(e.currentTarget.files ?? []);
              e.currentTarget.value = '';
              addFiles(files);
            }}
          />
        </section>
      )}
      <ErrorAlert error={error} />
      <div className="dialog-actions">
        {project && (
          <button type="button" className="secondary danger-text" disabled={busy} onClick={remove}>
            Delete project
          </button>
        )}
        <button type="button" className="secondary" onClick={onClose}>
          Cancel
        </button>
        <button className="primary" form="project-form" disabled={busy || !name.trim()}>
          {project ? 'Save' : 'Create project'}
        </button>
      </div>
    </Dialog>
  );
}
