import { createContext, useContext, useEffect, useRef, useState, type ReactNode } from 'react';
import type { AttachmentRef } from '../api';
import { Icon } from '../icons';

// Photos open over the chat instead of as a bare page, which a home-screen app on iPhone has no way back
// from. They close with ×, a tap beside the photo, a swipe down, Escape or the back gesture.
const OpenImage = createContext<(image: AttachmentRef) => void>(() => {});

export function useOpenImage() {
  return useContext(OpenImage);
}

export function ImageViewerProvider({ children }: { children: ReactNode }) {
  const [image, setImage] = useState<AttachmentRef | null>(null);
  return (
    <OpenImage.Provider value={setImage}>
      {children}
      <ImageViewer image={image} onClosed={() => setImage(null)} />
    </OpenImage.Provider>
  );
}

// Phones save a photo through the share sheet (Spara bild on iPhone); elsewhere it is downloaded.
const canShareFiles = (() => {
  try {
    return !!navigator.canShare?.({ files: [new File([''], 'photo.jpg', { type: 'image/jpeg' })] });
  } catch {
    return false;
  }
})();

function ImageViewer({ image, onClosed }: { image: AttachmentRef | null; onClosed: () => void }) {
  const dialog = useRef<HTMLDialogElement>(null);
  const [file, setFile] = useState<File | null>(null);
  const [drag, setDrag] = useState(0);
  const touchStart = useRef<number | null>(null);
  const url = image ? `/api/uploads/${image.id}` : '';

  // Closing also uses up the history entry added on opening, when it is still on top; without it the
  // photo just closes, so closing can never leave the app.
  function close() {
    dialog.current?.close();
    onClosed();
    if (history.state?.photo) {
      history.back();
    }
  }

  // Opening adds a history entry, so the back gesture or button closes the photo rather than the app.
  useEffect(() => {
    const element = dialog.current;
    if (!image || !element) {
      return;
    }
    element.showModal();
    history.pushState({ photo: image.id }, '');
    // Done here rather than on the dialog's close event, which a hidden page may hold back.
    const onBack = () => {
      element.close();
      onClosed();
    };
    window.addEventListener('popstate', onBack);
    return () => window.removeEventListener('popstate', onBack);
  }, [image]);

  // The share sheet must open straight from the tap, so the file is fetched in advance.
  useEffect(() => {
    setFile(null);
    setDrag(0);
    if (!image || !canShareFiles) {
      return;
    }
    let current = true;
    fetch(`/api/uploads/${image.id}`)
      .then((response) => response.blob())
      .then((blob) => {
        if (current) {
          setFile(new File([blob], image.name, { type: blob.type || image.mime }));
        }
      })
      .catch(() => {});
    return () => {
      current = false;
    };
  }, [image]);

  return (
    <dialog
      ref={dialog}
      className="image-viewer"
      aria-label={image?.name ?? 'Photo'}
      // Closed by the browser itself (such as Android's back button). A close event that arrives late,
      // after the photo was opened again, is ignored.
      onClose={() => {
        if (!dialog.current?.open) {
          onClosed();
        }
      }}
      onCancel={(e) => {
        e.preventDefault();
        close();
      }}
      onClick={(e) => {
        // A tap beside the photo, not on it or a button.
        const target = e.target as Element;
        if (target === e.currentTarget || target.classList.contains('viewer-stage')) {
          close();
        }
      }}
      onTouchStart={(e) => {
        touchStart.current = e.touches.length === 1 ? e.touches[0].clientY : null;
      }}
      onTouchMove={(e) => {
        if (touchStart.current !== null && e.touches.length === 1) {
          setDrag(Math.max(0, e.touches[0].clientY - touchStart.current));
        }
      }}
      onTouchEnd={() => {
        touchStart.current = null;
        if (drag > 100) {
          close();
        } else {
          setDrag(0);
        }
      }}
    >
      {image && (
        <>
          <div className="viewer-bar">
            <span className="viewer-name">{image.name}</span>
            {file ? (
              <button
                type="button"
                className="viewer-button"
                aria-label="Save or share the photo"
                title="Save or share"
                onClick={() => void navigator.share({ files: [file] }).catch(() => {})}
              >
                <Icon name="share" size={20} />
              </button>
            ) : (
              !canShareFiles && (
                <a
                  className="viewer-button"
                  href={url}
                  download={image.name}
                  aria-label="Download the photo"
                  title="Download"
                >
                  <Icon name="share" size={20} />
                </a>
              )
            )}
            <button
              type="button"
              className="viewer-button"
              aria-label="Close"
              title="Close"
              onClick={close}
            >
              <Icon name="close" size={20} />
            </button>
          </div>
          <div
            className="viewer-stage"
            style={drag ? { opacity: Math.max(0.3, 1 - drag / 400) } : undefined}
          >
            <img
              src={url}
              alt={image.name}
              style={drag ? { transform: `translateY(${drag}px)` } : undefined}
            />
          </div>
        </>
      )}
    </dialog>
  );
}
