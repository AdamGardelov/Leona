import type { AttachmentRef } from './api';

export type PendingAttachment = {
  key: string;
  name: string;
  kind: 'image' | 'document';
  size: number;
  previewUrl?: string;
  status: 'uploading' | 'ready' | 'error';
  ref?: AttachmentRef;
  error?: string;
};

export const documentTypes = '.pdf,.docx,.txt,.md,.csv,.json,.log';
const maxImageSide = 1600;

// Photos are scaled down and re-encoded as JPEG in the browser: smaller uploads, fewer image
// tokens, and formats like HEIC or WebP become something every vision model can read.
async function toJpeg(file: File): Promise<Blob> {
  const bitmap = await createImageBitmap(file);
  const scale = Math.min(1, maxImageSide / Math.max(bitmap.width, bitmap.height));
  const canvas = document.createElement('canvas');
  canvas.width = Math.round(bitmap.width * scale);
  canvas.height = Math.round(bitmap.height * scale);
  const context = canvas.getContext('2d');
  if (!context) {
    throw new Error('This browser cannot prepare images.');
  }
  context.fillStyle = '#fff';
  context.fillRect(0, 0, canvas.width, canvas.height);
  context.drawImage(bitmap, 0, 0, canvas.width, canvas.height);
  bitmap.close();
  return await new Promise((resolve, reject) =>
    canvas.toBlob(
      (blob) => (blob ? resolve(blob) : reject(new Error('Could not prepare the image.'))),
      'image/jpeg',
      0.85,
    ),
  );
}

export function describeSize(bytes: number) {
  if (bytes < 1024) {
    return `${bytes} B`;
  }
  return bytes < 1024 * 1024
    ? `${Math.round(bytes / 1024)} KB`
    : `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

export function isImage(file: File) {
  return file.type.startsWith('image/');
}

// Prepares and uploads one file; the returned reference is what a run receives.
export async function uploadFile(file: File): Promise<{ ref: AttachmentRef; previewUrl?: string }> {
  let body: Blob = file;
  let name = file.name || 'pasted-image.png';
  let previewUrl: string | undefined;
  if (isImage(file)) {
    body = await toJpeg(file);
    name = name.replace(/\.[^.]+$/, '') + '.jpg';
    previewUrl = URL.createObjectURL(body);
  }
  const form = new FormData();
  form.append('file', body, name);
  const response = await fetch('/api/uploads', { method: 'POST', body: form });
  const data = await response.json().catch(() => undefined);
  if (!response.ok) {
    throw new Error(data?.error ?? `Upload failed (${response.status}).`);
  }
  return { ref: data as AttachmentRef, previewUrl };
}
