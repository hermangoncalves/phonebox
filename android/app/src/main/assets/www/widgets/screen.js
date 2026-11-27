import { bridge, curPage, h, pages, t, tp } from '../main.js';
import { W } from '../registry.js';

let nextViewer = 1;
const viewers = new Map(); // viewer -> { onEvent }

function b64ToU8(b64) {
  const bin = atob(b64);
  const out = new Uint8Array(bin.length);
  for (let i = 0; i < bin.length; i++) out[i] = bin.charCodeAt(i);
  return out;
}

window.__phoneboxScreen = (viewer, eventJson, b64) => {
  const v = viewers.get(+viewer);
  if (!v) return;
  let ev;
  try { ev = JSON.parse(eventJson); } catch (_) { return; }
  v.onEvent(ev, b64 || null);
};

W.screen = {
  create(box, { o, page, w }) {
    box.classList.add('screen-widget');
    const video = document.createElement('video');
    video.className = 'screen-video';
    video.autoplay = true;
    video.muted = true;
    video.playsInline = true;
    video.setAttribute('playsinline', '');
    const note = h('div', 'empty screen-note', tp('Connecting…'));
    box.append(video, note);

    const widgetKey = `${page.id}/${w.id}`;
    const viewer = nextViewer++;
    let alive = true, opened = false, ready = false;
    let ms = null, sb = null, queue = [];
    let appending = false, lastEpoch = -1;
    let hbTimer = 0, lastFrameAt = 0;
    let wantControl = !!o.control;

    const say = (text) => { note.textContent = text || ''; note.hidden = !text; };
    const visible = () => alive && box.isConnected && !document.hidden
      && !document.body.classList.contains('night')
      && pages[curPage] === page && !box.closest('.stack-item:not(.active)');
    // page visibility: only when this widget's page is current - handled by update/destroy pattern
    // but we also check in visible(); main.js toggles pages. We rely on update() being called and on visibility checks.

    function teardownMedia(keepLast) {
      try { if (hbTimer) { clearInterval(hbTimer); hbTimer = 0; } } catch (_) {}
      queue = []; appending = false; ready = false;
      if (!keepLast) {
        try { video.removeAttribute('src'); video.load(); } catch (_) {}
      }
      try { if (ms && ms.readyState === 'open') ms.endOfStream(); } catch (_) {}
      sb = null; ms = null;
    }

    function closeSocket() {
      if (opened && bridge?.screenClose) { try { bridge.screenClose(viewer); } catch (_) {} }
      opened = false;
      viewers.delete(viewer);
    }

    function pump() {
      if (!sb || appending || !queue.length) return;
      if (sb.updating) return;
      const item = queue.shift();
      appending = true;
      try {
        sb.appendBuffer(item.buf);
        // ack on updateend
        sb._pendingAck = item;
      } catch (e) {
        appending = false;
        sb._pendingAck = null;
        // quota/buffer error - try to recover by trimming
        try {
          if (sb.buffered.length > 0) {
            const start = sb.buffered.start(0);
            const now = video.currentTime || 0;
            if (now - start > 30) { sb.remove(start, now - 10); return; }
          }
        } catch (_) {}
        say(tp('Playback error'));
      }
    }

    function setupMse(codec) {
      teardownMedia(false);
      const mime = `video/mp4; codecs="${codec || 'avc1.42C028'}"`;
      if (!window.MediaSource || !MediaSource.isTypeSupported(mime)) {
        say(tp('Video not supported on this device'));
        return;
      }
      ms = new MediaSource();
      video.src = URL.createObjectURL(ms);
      ms.addEventListener('sourceopen', () => {
        if (!ms || ms.readyState !== 'open') return;
        try {
          sb = ms.addSourceBuffer(mime);
          sb.mode = 'segments';
          sb.addEventListener('updateend', () => {
            appending = false;
            const ack = sb && sb._pendingAck;
            if (sb) sb._pendingAck = null;
            if (ack && bridge?.screenAck) {
              try { bridge.screenAck(viewer, ack.epoch, ack.seq); } catch (_) {}
            }
            pump();
          });
          sb.addEventListener('error', () => { appending = false; });
          ready = true;
          say('');
          pump();
        } catch (e) {
          say(tp('Video not supported on this device'));
        }
      }, { once: true });
    }

    function onEvent(ev, b64) {
      if (!alive) return;
      if (ev.type === 'ready') {
        lastEpoch = -1;
        queue = [];
        setupMse(ev.codec || 'avc1.42C028');
        if (ev.inputAvailable && wantControl) {
          // touch enabled - handled by pointer events below
        }
        return;
      }
      if (ev.type === 'error') {
        say(tp('Screen unavailable'));
        return;
      }
      if (ev.type === 'media' && b64) {
        if (!ready || !sb) return;
        const epoch = +ev.epoch || 0;
        const seq = +ev.seq || 0;
        if (lastEpoch !== -1 && epoch !== lastEpoch) {
          // epoch changed - reset buffer
          lastEpoch = epoch;
          queue = [];
          try {
            if (sb.updating) sb.abort();
            if (sb.buffered.length > 0) sb.remove(0, Infinity);
          } catch (_) {}
          // the init for the new epoch will arrive as next media; just queue it
        }
        if (lastEpoch === -1) lastEpoch = epoch;
        try {
          const buf = b64ToU8(b64);
          queue.push({ buf, epoch, seq });
          lastFrameAt = Date.now();
          pump();
        } catch (_) {}
        return;
      }
      if (ev.type === 'stats') {
        // optional: could show fps
        return;
      }
    }

    function openSocket() {
      if (opened || !bridge?.screenOpen) {
        if (!bridge?.screenOpen) say(tp('Screen needs the Android app'));
        return;
      }
      viewers.set(viewer, { onEvent });
      opened = true;
      say(tp('Connecting…'));
      try { bridge.screenOpen(widgetKey, viewer); } catch (_) { say(tp('Screen unavailable')); }
      hbTimer = setInterval(() => {
        if (!opened || !bridge?.screenSend) return;
        try { bridge.screenSend(viewer, JSON.stringify({ op: 'heartbeat' })); } catch (_) {}
      }, 3000);
    }

    function closeAll() {
      closeSocket();
      teardownMedia(true);
    }

    // touch forwarding when control enabled
    function sendOp(op) {
      if (!opened || !bridge?.screenSend) return;
      try { bridge.screenSend(viewer, JSON.stringify(op)); } catch (_) {}
    }
    const onDown = (e) => {
      if (!wantControl) return;
      const rect = video.getBoundingClientRect();
      if (!rect.width || !rect.height) return;
      const x = (e.clientX - rect.left) / rect.width;
      const y = (e.clientY - rect.top) / rect.height;
      if (x < 0 || x > 1 || y < 0 || y > 1) return;
      video.setPointerCapture(e.pointerId);
      sendOp({ op: 'pointer', x, y });
      sendOp({ op: 'button', n: 1, down: true });
      e.preventDefault();
    };
    const onMove = (e) => {
      if (!wantControl) return;
      const rect = video.getBoundingClientRect();
      if (!rect.width || !rect.height) return;
      const x = Math.max(0, Math.min(1, (e.clientX - rect.left) / rect.width));
      const y = Math.max(0, Math.min(1, (e.clientY - rect.top) / rect.height));
      sendOp({ op: 'pointer', x, y });
    };
    const onUp = (e) => {
      if (!wantControl) return;
      sendOp({ op: 'button', n: 1, down: false });
      sendOp({ op: 'releaseAll' });
    };
    video.addEventListener('pointerdown', onDown);
    video.addEventListener('pointermove', onMove);
    video.addEventListener('pointerup', onUp);
    video.addEventListener('pointercancel', onUp);

    let wasVisible = false;
    return {
      update() {
        const v = visible();
        if (v && !wasVisible) openSocket();
        if (!v && wasVisible) closeAll();
        wasVisible = v;
      },
      destroy() {
        alive = false;
        closeAll();
        video.removeEventListener('pointerdown', onDown);
        video.removeEventListener('pointermove', onMove);
        video.removeEventListener('pointerup', onUp);
        video.removeEventListener('pointercancel', onUp);
      },
    };
  },
};
