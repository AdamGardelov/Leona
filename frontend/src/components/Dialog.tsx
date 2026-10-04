import { useEffect, useRef, type ReactNode } from 'react';
import { Icon } from '../icons';

// A native modal dialog: the browser handles focus trapping, Escape and the backdrop.
export function Dialog({
  title,
  open,
  wide,
  onClose,
  children,
}: {
  title: string;
  open: boolean;
  wide?: boolean;
  onClose: () => void;
  children: ReactNode;
}) {
  const dialog = useRef<HTMLDialogElement>(null);

  useEffect(() => {
    const element = dialog.current;
    if (!element) {
      return;
    }
    if (open && !element.open) {
      element.showModal();
      // Focus the dialog itself rather than its first button, so screen readers read the title and
      // phones show no focus ring on the close button.
      element.focus();
    } else if (!open && element.open) {
      element.close();
    }
  }, [open]);

  return (
    <dialog
      ref={dialog}
      className={wide ? 'dialog wide' : 'dialog'}
      tabIndex={-1}
      aria-labelledby="dialog-title"
      onClose={onClose}
      onClick={(e) => {
        // A click on the backdrop lands on the dialog element itself.
        if (e.target === dialog.current) {
          onClose();
        }
      }}
    >
      {open && (
        <div className="dialog-body">
          <header className="dialog-head">
            <h2 id="dialog-title">{title}</h2>
            <button className="panel-toggle" aria-label="Close" onClick={onClose}>
              <Icon name="close" size={18} />
            </button>
          </header>
          {children}
        </div>
      )}
    </dialog>
  );
}
