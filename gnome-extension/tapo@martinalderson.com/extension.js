// Tapo Plugs — a GNOME Quick Settings tile that controls TP-Link Tapo plugs
// by shelling out to the `tapo` CLI (which does discovery + KLAP locally).
//
// The shell itself does no crypto or network beyond spawning `tapo … --json`
// asynchronously via Gio.Subprocess, so it never blocks.

import GObject from 'gi://GObject';
import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

import {Extension} from 'resource:///org/gnome/shell/extensions/extension.js';
import * as Main from 'resource:///org/gnome/shell/ui/main.js';
import * as QuickSettings from 'resource:///org/gnome/shell/ui/quickSettings.js';
import * as PopupMenu from 'resource:///org/gnome/shell/ui/popupMenu.js';

Gio._promisify(Gio.Subprocess.prototype, 'communicate_utf8_async');

// Locate the `tapo` binary: PATH first, then common install locations.
function findBinary() {
    const inPath = GLib.find_program_in_path('tapo');
    if (inPath)
        return inPath;
    const candidates = [
        GLib.build_filenamev([GLib.get_home_dir(), '.local', 'bin', 'tapo']),
        '/usr/local/bin/tapo',
        '/usr/bin/tapo',
    ];
    return candidates.find(c => GLib.file_test(c, GLib.FileTest.IS_EXECUTABLE)) ?? null;
}

const TapoToggle = GObject.registerClass(
class TapoToggle extends QuickSettings.QuickMenuToggle {
    constructor(extension) {
        super({
            title: 'Tapo',
            subtitle: 'Smart plugs',
            gicon: extension.tapoIcon,
            toggleMode: true,
        });

        this._bin = extension.tapoBin;

        this.menu.setHeader(extension.tapoIcon, 'Tapo Plugs');

        this._deviceSection = new PopupMenu.PopupMenuSection();
        this.menu.addMenuItem(this._deviceSection);

        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this.menu.addAction('Rescan network', () => this._rescan());

        // Clicking the tile body switches every plug at once.
        this.connect('clicked', () => this._toggleAll());
        // Refresh live state whenever the dropdown opens.
        this.menu.connect('open-state-changed', (_m, isOpen) => {
            if (isOpen)
                this._refresh();
        });

        this._refresh();
    }

    async _run(args) {
        if (!this._bin)
            throw new Error('tapo binary not found (PATH or ~/.local/bin)');
        const proc = Gio.Subprocess.new(
            [this._bin, ...args, '--json'],
            Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_PIPE);
        const [stdout, stderr] = await proc.communicate_utf8_async(null, null);
        if (!proc.get_successful())
            throw new Error((stderr || '').trim() || `tapo ${args[0]} failed`);
        return (stdout && stdout.trim()) ? JSON.parse(stdout) : null;
    }

    async _refresh() {
        try {
            const devices = await this._run(['status']);
            this._populate(devices ?? []);
        } catch (e) {
            this._showMessage(e.message);
        }
    }

    _populate(devices) {
        this._deviceSection.removeAll();

        if (devices.length === 0) {
            this._addInfo('No devices — run “tapo discover”');
            this.subtitle = 'No devices';
            this.checked = false;
            return;
        }

        let onCount = 0;
        for (const d of devices) {
            const name = d.nickname || d.model || d.ip;
            const on = d.device_on === true;
            if (!d.reachable) {
                const item = new PopupMenu.PopupMenuItem(`${name}  (offline)`);
                item.setSensitive(false);
                this._deviceSection.addMenuItem(item);
                continue;
            }
            const item = new PopupMenu.PopupMenuItem(name);
            item.setOrnament(on ? PopupMenu.Ornament.CHECK : PopupMenu.Ornament.NONE);
            item.connect('activate', () => this._toggleOne(d.device_id));
            this._deviceSection.addMenuItem(item);
            if (on)
                onCount++;
        }

        this.subtitle = `${onCount} of ${devices.length} on`;
        this.checked = onCount > 0;
    }

    _showMessage(message) {
        this._deviceSection.removeAll();
        const text = message.toLowerCase().includes('not logged in')
            ? 'Run “tapo login” in a terminal'
            : `Error: ${message}`;
        this._addInfo(text);
        this.subtitle = 'Not set up';
        this.checked = false;
    }

    _addInfo(text) {
        const item = new PopupMenu.PopupMenuItem(text);
        item.setSensitive(false);
        this._deviceSection.addMenuItem(item);
    }

    async _toggleOne(deviceId) {
        try {
            await this._run(['toggle', deviceId]);
        } catch (e) {
            Main.notifyError('Tapo', e.message);
        }
    }

    async _toggleAll() {
        // `checked` has already flipped to the desired state by toggle-mode.
        try {
            await this._run(['all', this.checked ? 'on' : 'off']);
        } catch (e) {
            Main.notifyError('Tapo', e.message);
        }
    }

    async _rescan() {
        this.subtitle = 'Scanning…';
        try {
            await this._run(['discover']);
            await this._refresh();
        } catch (e) {
            this._showMessage(e.message);
        }
    }
});

const TapoIndicator = GObject.registerClass(
class TapoIndicator extends QuickSettings.SystemIndicator {
    constructor(extension) {
        super();
        this._toggle = new TapoToggle(extension);
        this.quickSettingsItems.push(this._toggle);
    }

    destroy() {
        this._toggle.destroy();
        super.destroy();
    }
});

export default class TapoExtension extends Extension {
    enable() {
        this.tapoBin = findBinary();
        this.tapoIcon = Gio.icon_new_for_string(`${this.path}/icons/tapo-symbolic.svg`);
        this._indicator = new TapoIndicator(this);

        const quickSettings = Main.panel.statusArea.quickSettings;
        quickSettings.addExternalIndicator(this._indicator);

        // addExternalIndicator appends the tile to the end of the grid (below the
        // brightness sliders), and its exact spot drifts with load order. Pin it
        // up among the other toggles, just above the brightness section.
        const toggle = this._indicator.quickSettingsItems[0];
        const grid = quickSettings.menu._grid;
        const sibling = quickSettings._brightness?.quickSettingsItems?.[0];
        if (sibling && toggle?.get_parent() === grid)
            grid.set_child_below_sibling(toggle, sibling);
    }

    disable() {
        this._indicator?.destroy();
        this._indicator = null;
        this.tapoIcon = null;
        this.tapoBin = null;
    }
}
