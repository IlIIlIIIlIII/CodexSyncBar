import Gio from 'gi://Gio';
import GLib from 'gi://GLib';

// Asynchronous, bounded transport: never block the GNOME Shell main thread.
export class Client {
    constructor(path = GLib.build_filenamev([GLib.get_user_runtime_dir(), 'claude-syncbar', 'control.sock'])) {
        this.path = path;
        this.pending = new Set();
    }

    async call(method, params = {}) {
        const cancel = new Gio.Cancellable();
        this.pending.add(cancel);
        let timeout = GLib.timeout_add_seconds(GLib.PRIORITY_DEFAULT, 40, () => {
            timeout = 0; cancel.cancel(); return GLib.SOURCE_REMOVE;
        });
        let connection;
        try {
            const socket = new Gio.SocketClient({timeout: 35});
            connection = await new Promise((resolve, reject) => socket.connect_async(
                new Gio.UnixSocketAddress({path: this.path}), cancel, (source, result) => {
                    try { resolve(source.connect_finish(result)); } catch (error) { reject(error); }
                }));
            if (connection.get_socket().get_credentials().get_unix_user() !== new Gio.Credentials().get_unix_user())
                throw new Error('다른 사용자의 서비스에는 연결할 수 없습니다.');
            const id = GLib.uuid_string_random();
            const request = new TextEncoder().encode(JSON.stringify({id, method, params}) + '\n');
            await new Promise((resolve, reject) => connection.output_stream.write_all_async(request,
                GLib.PRIORITY_DEFAULT, cancel, (source, result) => {
                    try { resolve(source.write_all_finish(result)); } catch (error) { reject(error); }
                }));
            const chunks = [];
            let length = 0, complete = false;
            while (!complete) {
                const bytes = await new Promise((resolve, reject) => connection.input_stream.read_bytes_async(
                    8192, GLib.PRIORITY_DEFAULT, cancel, (source, result) => {
                        try { resolve(source.read_bytes_finish(result).get_data()); } catch (error) { reject(error); }
                    }));
                if (!bytes?.length) throw new Error('서비스 응답이 끊겼습니다.');
                length += bytes.length;
                if (length > 4 * 1024 * 1024) throw new Error('서비스 응답이 너무 큽니다.');
                chunks.push(bytes);
                complete = bytes.includes(10);
            }
            const bytes = new Uint8Array(length);
            let offset = 0;
            for (const chunk of chunks) { bytes.set(chunk, offset); offset += chunk.length; }
            const response = JSON.parse(new TextDecoder().decode(bytes).split('\n')[0]);
            if (response.id !== id) throw new Error('서비스 응답을 확인하지 못했습니다.');
            if (response.ok !== true) throw new Error(response.error?.message || '작업을 완료하지 못했습니다.');
            return response.result;
        } finally {
            if (timeout) GLib.source_remove(timeout);
            cancel.cancel();
            this.pending.delete(cancel);
            connection?.close_async(GLib.PRIORITY_DEFAULT, null, (source, result) => {
                try { source.close_finish(result); } catch { /* Already disconnected. */ }
            });
        }
    }

    destroy() { for (const cancel of this.pending) cancel.cancel(); this.pending.clear(); }
}
