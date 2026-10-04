// Leona in the top bar: the mark is mint while the backend runs and grey when it is off. The menu opens
// Leona, switches it on or off, restarts it (never while an answer is being written) and sets autostart.
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import Gio from 'gi://Gio';
import Soup from 'gi://Soup?version=3.0';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

const SERVICE = 'leona.service';
const URL = 'http://localhost:5080';

// Runs a command and resolves with whether it succeeded and what it printed.
function run(argv) {
    return new Promise(resolve => {
        try {
            const process = Gio.Subprocess.new(argv,
                Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_MERGE);
            process.communicate_utf8_async(null, null, (proc, result) => {
                try {
                    const [, output] = proc.communicate_utf8_finish(result);
                    resolve({ok: proc.get_successful(), output: (output ?? '').trim()});
                } catch {
                    resolve({ok: false, output: ''});
                }
            });
        } catch {
            resolve({ok: false, output: ''});
        }
    });
}

const systemctl = (...args) => run(['systemctl', '--user', ...args, SERVICE]);

const LeonaIndicator = GObject.registerClass(
class LeonaIndicator extends PanelMenu.Button {
    _init(extension) {
        super._init(0.5, 'Leona');
        this._extension = extension;
        this._session = new Soup.Session({timeout: 3});
        this._onIcon = Gio.FileIcon.new(extension.dir.get_child('icons').get_child('leona-on.svg'));
        this._offIcon = Gio.FileIcon.new(extension.dir.get_child('icons').get_child('leona-off.svg'));
        this._icon = new St.Icon({gicon: this._offIcon, style_class: 'system-status-icon'});
        this.add_child(this._icon);

        this._status = new PopupMenu.PopupMenuItem('Checking…', {reactive: false});
        this._status.label.add_style_class_name('leona-status');
        this.menu.addMenuItem(this._status);
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());

        this._open = new PopupMenu.PopupMenuItem('Open Leona');
        this._open.connect('activate', () => Gio.AppInfo.launch_default_for_uri(URL, null));
        this.menu.addMenuItem(this._open);

        this._switch = new PopupMenu.PopupSwitchMenuItem('Leona', false);
        this._switch.connect('toggled', (_item, on) => this._setRunning(on));
        this.menu.addMenuItem(this._switch);

        this._restart = new PopupMenu.PopupMenuItem('Restart');
        this._restart.connect('activate', () => this._restartSafely());
        this.menu.addMenuItem(this._restart);

        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._autostart = new PopupMenu.PopupSwitchMenuItem('Start at login', false);
        this._autostart.connect('toggled', async (_item, on) => {
            await systemctl(on ? 'enable' : 'disable');
            this._refresh();
        });
        this.menu.addMenuItem(this._autostart);

        this.menu.connect('open-state-changed', (_menu, open) => {
            if (open)
                this._refresh();
        });
        this._timer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 5, () => {
            this._refresh();
            return GLib.SOURCE_CONTINUE;
        });
        this._refresh();
    }

    // Answers being written right now, across every profile; null when Leona does not answer.
    _activeRuns() {
        return new Promise(resolve => {
            const message = Soup.Message.new('GET', `${URL}/api/status`);
            this._session.send_and_read_async(message, GLib.PRIORITY_DEFAULT, null, (session, result) => {
                try {
                    const bytes = session.send_and_read_finish(result);
                    if (message.get_status() !== 200) {
                        resolve(null);
                        return;
                    }
                    resolve(JSON.parse(new TextDecoder().decode(bytes.get_data())).activeRuns ?? 0);
                } catch {
                    resolve(null);
                }
            });
        });
    }

    async _refresh() {
        const [{output: state}, {output: enabled}] = await Promise.all([
            systemctl('is-active'), systemctl('is-enabled'),
        ]);
        const active = await this._activeRuns();
        const on = state === 'active';
        this._icon.gicon = on && active !== null ? this._onIcon : this._offIcon;
        this._switch.setToggleState(on || state === 'activating');
        this._autostart.setToggleState(enabled === 'enabled');
        this._open.setSensitive(on);
        this._restart.setSensitive(on);
        this._status.label.text =
            state === 'activating' || (on && active === null) ? 'Starting…'
            : on ? active > 0 ? `Running · answering ${active === 1 ? 'a question' : `${active} questions`}` : 'Running'
            : state === 'failed' ? 'Stopped after an error'
            : 'Off';
    }

    async _setRunning(on) {
        await systemctl(on ? 'start' : 'stop');
        this._refresh();
    }

    // A restart would cut off an answer being written, so it waits until nothing runs.
    async _restartSafely() {
        const active = await this._activeRuns();
        if (active > 0) {
            Main.notify('Leona is answering', 'Restart when the answer is done.');
            return;
        }
        await systemctl('restart');
        this._refresh();
    }

    destroy() {
        if (this._timer) {
            GLib.source_remove(this._timer);
            this._timer = 0;
        }
        this._session?.abort();
        super.destroy();
    }
});

export default class LeonaExtension extends Extension {
    enable() {
        this._indicator = new LeonaIndicator(this);
        Main.panel.addToStatusArea(this.uuid, this._indicator);
    }

    disable() {
        this._indicator?.destroy();
        this._indicator = null;
    }
}
