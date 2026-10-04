import { Icon, PanelToggle } from '../icons';
import type { TimelineItem, Usage } from '../useRun';

type ActivityPanelProps = {
  busy: boolean;
  usage: Usage | null;
  timeline: TimelineItem[];
  supportsTools: boolean;
  canInspect: boolean;
  onInspect: () => void;
  onTogglePanel: () => void;
};

export function ActivityPanel({
  busy,
  usage,
  timeline,
  supportsTools,
  canInspect,
  onInspect,
  onTogglePanel,
}: ActivityPanelProps) {
  const used = usage?.prompt ?? usage?.estimate ?? 0;
  const nearLimit = usage !== null && usage.window > 0 && used >= usage.window * 0.85;
  const last = timeline.length - 1;
  return (
    <section id="activity-panel" className="activity" aria-label="Activity">
      <div className="activity-head">
        <h2 className="label">Activity</h2>
        <PanelToggle side="right" open onToggle={onTogglePanel} />
      </div>
      {usage && usage.window > 0 && (
        <div className={nearLimit ? 'card usage near-limit' : 'card usage'} role="status">
          <div className="usage-row">
            <strong>Context</strong>
            <span className="mono">
              {usage.prompt !== undefined
                ? usage.prompt.toLocaleString()
                : `≈${usage.estimate?.toLocaleString()}`}{' '}
              / {usage.window.toLocaleString()}
            </span>
          </div>
          <div className="meter" aria-hidden="true">
            <span style={{ width: `${Math.min(100, (used / usage.window) * 100)}%` }} />
          </div>
          <p>
            {usage.prompt !== undefined
              ? 'Measured prompt tokens from Ollama.'
              : 'Estimated. Measured count arrives after this step.'}
          </p>
          {nearLimit && <p>Near the context limit. Older context may be shortened.</p>}
          {usage.outputTokens !== undefined && usage.outputTokens > 0 && (
            <div className="usage-stats">
              <span>
                <strong>{usage.outputTokens.toLocaleString()}</strong> output tokens
              </span>
              {usage.tokensPerSecond !== undefined && (
                <span>
                  <strong>{usage.tokensPerSecond.toFixed(1)}</strong> tok/s
                </span>
              )}
            </div>
          )}
        </div>
      )}
      {timeline.length ? (
        <div>
          <h3>Timeline</h3>
          <ol className="timeline">
            {timeline.map((item, i) => {
              const current =
                item.state === 'running' ||
                item.state === 'waiting' ||
                (busy && i === last && item.state === 'info');
              return (
                <li key={item.key} className={current ? `current ${item.state}` : item.state}>
                  {item.text}
                </li>
              );
            })}
          </ol>
        </div>
      ) : (
        <div className="empty">Activity will appear here when you send a message.</div>
      )}
      {canInspect && (
        <button className="inspect-button" onClick={onInspect}>
          <Icon name="inspect" />
          Inspect run
        </button>
      )}
      <div className="tools">
        <h3>{supportsTools ? 'Tools available' : 'Chat-only model'}</h3>
        <p>Web: search and page reading.</p>
        <p>Files: search, read documents, create and edit. Changes need your approval.</p>
        <p>Terminal: commands run only after you approve each one.</p>
      </div>
    </section>
  );
}
