// Tailscale in the top bar: the icon shows whether this computer is connected, the switch runs
// `tailscale up` or `tailscale down`, and the menu lists the other devices (click one to copy its name).
import GLib from 'gi://GLib';
import GObject from 'gi://GObject';
import Gio from 'gi://Gio';
import St from 'gi://St';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as PanelMenu from 'resource:///org/gnome/shell/ui/panelMenu.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';
import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';

const ADMIN = 'https://login.tailscale.com/admin/machines';

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
            resolve({ok: false, output: 'tailscale was not found'});
        }
    });
}

// "iphone16-adam" from "iphone16-adam.tailnet.ts.net."; phones often report "localhost" as host name.
function nameOf(node) {
    const dns = (node.DNSName ?? '').split('.')[0];
    return dns || node.HostName || 'Unknown device';
}

function ago(timestamp) {
    const time = Date.parse(timestamp ?? '');
    if (!time || time < Date.UTC(2000, 0, 1))
        return '';
    const minutes = Math.max(1, Math.round((Date.now() - time) / 60000));
    if (minutes < 60)
        return `${minutes} min ago`;
    if (minutes < 48 * 60)
        return `${Math.round(minutes / 60)} h ago`;
    return `${Math.round(minutes / 1440)} days ago`;
}

const TailscaleIndicator = GObject.registerClass(
class TailscaleIndicator extends PanelMenu.Button {
    _init() {
        super._init(0.5, 'Tailscale');
        this._icon = new St.Icon({icon_name: 'network-vpn-disabled-symbolic', style_class: 'system-status-icon'});
        this.add_child(this._icon);

        this._status = new PopupMenu.PopupMenuItem('Checking…', {reactive: false});
        this._status.label.add_style_class_name('tailscale-status');
        this.menu.addMenuItem(this._status);

        this._switch = new PopupMenu.PopupSwitchMenuItem('Tailscale', false);
        this._switch.connect('toggled', (_item, on) => this._setConnected(on));
        this.menu.addMenuItem(this._switch);

        this._self = new PopupMenu.PopupMenuItem('');
        this._self.connect('activate', () => this._copy(this._selfAddress));
        this.menu.addMenuItem(this._self);

        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem('Devices'));
        this._devices = new PopupMenu.PopupMenuSection();
        this.menu.addMenuItem(this._devices);

        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        const admin = new PopupMenu.PopupMenuItem('Admin console');
        admin.connect('activate', () => Gio.AppInfo.launch_default_for_uri(ADMIN, null));
        this.menu.addMenuItem(admin);

        this.menu.connect('open-state-changed', (_menu, open) => {
            if (open)
                this._refresh();
        });
        this._timer = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 10, () => {
            this._refresh();
            return GLib.SOURCE_CONTINUE;
        });
        this._refresh();
    }

    async _refresh() {
        const {ok, output} = await run(['tailscale', 'status', '--json']);
        let status = null;
        try {
            status = ok || output.startsWith('{') ? JSON.parse(output) : null;
        } catch {
            status = null;
        }

        const state = status?.BackendState ?? 'Unavailable';
        const connected = state === 'Running';
        this._icon.icon_name = connected ? 'network-vpn-symbolic'
            : state === 'Starting' ? 'network-vpn-acquiring-symbolic'
            : 'network-vpn-disabled-symbolic';
        this._switch.setToggleState(connected || state === 'Starting');
        this._switch.setSensitive(status !== null && state !== 'NeedsLogin');
        this._status.label.text = connected ? 'Connected'
            : state === 'Starting' ? 'Connecting…'
            : state === 'NeedsLogin' ? 'Needs login: run tailscale up in a terminal'
            : state === 'Stopped' ? 'Off'
            : 'Tailscale is not running';

        const self = status?.Self;
        this._selfAddress = self?.TailscaleIPs?.[0] ?? '';
        this._self.visible = connected && Boolean(self);
        if (self)
            this._self.label.text = `This computer: ${nameOf(self)} · ${this._selfAddress}`;

        this._devices.removeAll();
        const peers = Object.values(status?.Peer ?? {})
            .sort((a, b) => Number(b.Online) - Number(a.Online) || nameOf(a).localeCompare(nameOf(b)));
        if (!connected || peers.length === 0) {
            const empty = new PopupMenu.PopupMenuItem(connected ? 'No other devices' : 'Connect to see devices',
                {reactive: false});
            empty.label.add_style_class_name('tailscale-offline');
            this._devices.addMenuItem(empty);
            return;
        }

        for (const peer of peers) {
            const seen = peer.Online ? 'online' : ago(peer.LastSeen) || 'offline';
            const item = new PopupMenu.PopupMenuItem(`${nameOf(peer)} · ${peer.OS || 'device'} · ${seen}`);
            item.setOrnament(peer.Online ? PopupMenu.Ornament.DOT : PopupMenu.Ornament.NONE);
            if (!peer.Online)
                item.label.add_style_class_name('tailscale-offline');
            const address = (peer.DNSName ?? '').replace(/\.$/, '') || peer.TailscaleIPs?.[0] || '';
            item.connect('activate', () => this._copy(address));
            this._devices.addMenuItem(item);
        }
    }

    _copy(text) {
        if (!text)
            return;
        St.Clipboard.get_default().set_text(St.ClipboardType.CLIPBOARD, text);
        Main.notify('Copied', text);
    }

    // `tailscale up` and `down` need the user to be Tailscale's operator, which is set once with sudo.
    async _setConnected(on) {
        const {ok, output} = await run(['tailscale', on ? 'up' : 'down']);
        if (!ok) {
            const denied = /access denied|operator|permission/i.test(output);
            Main.notify(denied ? 'Tailscale needs permission once' : 'Tailscale did not respond',
                denied ? `Run in a terminal: sudo tailscale set --operator=${GLib.get_user_name()}` : output);
        }
        this._refresh();
    }

    destroy() {
        if (this._timer) {
            GLib.source_remove(this._timer);
            this._timer = 0;
        }
        super.destroy();
    }
});

export default class TailscaleStatusExtension extends Extension {
    enable() {
        this._indicator = new TailscaleIndicator();
        Main.panel.addToStatusArea(this.uuid, this._indicator);
    }

    disable() {
        this._indicator?.destroy();
        this._indicator = null;
    }
}
