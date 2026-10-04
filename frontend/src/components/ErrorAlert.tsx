// An error under a form or section; nothing when there is none.
export function ErrorAlert({ error }: { error: string }) {
  if (!error) {
    return null;
  }
  return (
    <div role="alert" className="error">
      {error}
    </div>
  );
}
