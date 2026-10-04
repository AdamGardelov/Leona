import React, { useEffect, useState } from 'react';
import { createRoot } from 'react-dom/client';
import './style.css';
import { App } from './App';
import { api, type Session, errorText } from './api';
import { PairScreen } from './components/PairScreen';
import { registerServiceWorker } from './components/NotificationsDialog';
import { followSystemTextSize } from './textSize';

// Other devices on the network see the pairing screen until they have a session.
function Root() {
  const [session, setSession] = useState<Session | null>(null);
  const [error, setError] = useState('');

  useEffect(() => {
    api<Session>('/session')
      .then(setSession)
      .catch((e) => setError(errorText(e)));
  }, []);

  if (error) {
    return (
      <div role="alert" className="error boot-error">
        {error}
      </div>
    );
  }
  if (!session) {
    return null;
  }
  return session.paired ? <App session={session} /> : <PairScreen />;
}

void registerServiceWorker();
followSystemTextSize();

createRoot(document.getElementById('root')!).render(
  <React.StrictMode>
    <Root />
  </React.StrictMode>,
);
