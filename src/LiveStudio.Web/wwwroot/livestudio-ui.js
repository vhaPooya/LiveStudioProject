(() => {
  const KEY = 'livestudio.ui.v1';
  const read = () => { try { return JSON.parse(localStorage.getItem(KEY) || '{}') || {}; } catch { return {}; } };
  window.lsUi = {
    get(key) { const v = read()[key]; return v === undefined ? null : v; },
    set(key, value) { const all = read(); all[key] = value; try { localStorage.setItem(KEY, JSON.stringify(all)); } catch (_) { } }
  };

  const PKEY = 'livestudio.prompter.v1';
  const defaults = { text: 'متن سناریوی خود را اینجا وارد کنید...\n\nبا دکمه شروع، متن به‌آرامی بالا می‌رود. سرعت، اندازه و شفافیت را از پنل تله‌پرومپتر تنظیم کنید.', speed: 45, size: 3.4, opacity: 72, position: 'bottom', mirror: false, lineHeight: 1.7 };
  const loops = new Map();
  let keyBound = false;

  const load = () => { try { return { ...defaults, ...(JSON.parse(localStorage.getItem(PKEY) || 'null') || {}) }; } catch { return { ...defaults }; } };
  const save = cfg => { try { localStorage.setItem(PKEY, JSON.stringify(cfg)); } catch (_) { } return cfg; };

  function stop(id) {
    const loop = loops.get(id); if (!loop) return false;
    cancelAnimationFrame(loop.frame); loops.delete(id); return true;
  }
  function start(id, speed, ref) {
    stop(id);
    const el = document.getElementById(id); if (!el) return false;
    const loop = { speed: Number(speed) || 45, last: 0, pos: el.scrollTop, frame: 0, ref };
    loops.set(id, loop);
    const tick = now => {
      const node = document.getElementById(id);
      if (!node || !loops.has(id)) { loops.delete(id); return; }
      if (loop.last) {
        if (Math.abs(node.scrollTop - loop.pos) > 3) loop.pos = node.scrollTop;
        const dt = Math.min((now - loop.last) / 1000, 0.05);
        loop.pos += loop.speed * dt * (node.clientWidth / 1000);
        node.scrollTop = loop.pos;
        if (node.scrollTop + node.clientHeight >= node.scrollHeight - 2) {
          loops.delete(id);
          try { loop.ref?.invokeMethodAsync('OnPrompterEnded'); } catch (_) { }
          return;
        }
      }
      loop.last = now; loop.frame = requestAnimationFrame(tick);
    };
    loop.frame = requestAnimationFrame(tick);
    return true;
  }
  const isTyping = el => {
    if (!el) return false;
    if (el.isContentEditable || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT') return true;
    if (el.tagName === 'INPUT') return !['range', 'checkbox', 'radio', 'color', 'button', 'file'].includes((el.type || '').toLowerCase());
    return false;
  };
  function bindKeys() {
    if (keyBound) return; keyBound = true;
    document.addEventListener('keydown', e => {
      if (!document.querySelector('.ls-tp-overlay') || isTyping(e.target)) return;
      if (e.code === 'Space') {
        e.preventDefault();
        if (e.repeat) return;
        if (document.activeElement && document.activeElement !== document.body) document.activeElement.blur?.();
        document.querySelector('.ls-tp-overlay [data-tp-toggle]')?.click();
      }
      else if (e.key === 'ArrowUp') { e.preventDefault(); document.querySelector('.ls-tp-overlay [data-tp-faster]')?.click(); }
      else if (e.key === 'ArrowDown') { e.preventDefault(); document.querySelector('.ls-tp-overlay [data-tp-slower]')?.click(); }
      else if (e.key === 'Home') { e.preventDefault(); document.querySelector('.ls-tp-overlay [data-tp-reset]')?.click(); }
    });
    document.addEventListener('keyup', e => {
      if (e.code === 'Space' && document.querySelector('.ls-tp-overlay') && !isTyping(e.target)) e.preventDefault();
    });
  }
  window.lsPrompter = {
    load, save, start, stop,
    setSpeed(id, speed) { const l = loops.get(id); if (l) l.speed = Number(speed) || l.speed; },
    isRunning(id) { return loops.has(id); },
    reset(id) { stop(id); const el = document.getElementById(id); if (el) el.scrollTop = 0; },
    bindKeys
  };

  // Floating, draggable and resizable panels (used by the studio chat).
  const FKEY = 'livestudio.float.v1';
  const fread = () => { try { return JSON.parse(localStorage.getItem(FKEY) || '{}') || {}; } catch { return {}; } };
  const fsave = (id, g) => { const a = fread(); a[id] = g; try { localStorage.setItem(FKEY, JSON.stringify(a)); } catch (_) { } };
  const fclamp = (g) => {
    const vw = window.innerWidth, vh = window.innerHeight;
    g.w = Math.min(Math.max(g.w, 280), vw - 16); g.h = Math.min(Math.max(g.h, 240), vh - 16);
    g.x = Math.min(Math.max(g.x, 8), vw - g.w - 8); g.y = Math.min(Math.max(g.y, 8), vh - g.h - 8);
    return g;
  };
  const apply = (el, g) => { el.style.left = g.x + 'px'; el.style.top = g.y + 'px'; el.style.width = g.w + 'px'; el.style.height = g.h + 'px'; el.style.right = 'auto'; el.style.bottom = 'auto'; };
  window.lsFloat = {
    open(id) {
      const el = document.getElementById(id); if (!el) return;
      const saved = fread()[id] || { x: 24, y: Math.max(24, window.innerHeight - 560), w: 340, h: 480 };
      const g = fclamp(saved); apply(el, g); el.dataset.geo = JSON.stringify(g);
      if (el.dataset.bound) return; el.dataset.bound = '1';
      const head = el.querySelector('[data-float-handle]'), grip = el.querySelector('[data-float-resize]');
      const geo = () => JSON.parse(el.dataset.geo || '{}');
      const track = (node, onMove) => node?.addEventListener('pointerdown', e => {
        if (!el.classList.contains('floating') || e.target.closest('button,input,select,textarea,a')) return;
        e.preventDefault(); node.setPointerCapture(e.pointerId);
        const start = { x: e.clientX, y: e.clientY, g: geo() };
        const move = ev => { const g = fclamp(onMove(start, ev)); apply(el, g); el.dataset.geo = JSON.stringify(g); };
        const up = () => { node.removeEventListener('pointermove', move); node.removeEventListener('pointerup', up); node.removeEventListener('pointercancel', up); fsave(id, geo()); };
        node.addEventListener('pointermove', move); node.addEventListener('pointerup', up); node.addEventListener('pointercancel', up);
      });
      track(head, (s, ev) => ({ ...s.g, x: s.g.x + ev.clientX - s.x, y: s.g.y + ev.clientY - s.y }));
      track(grip, (s, ev) => ({ ...s.g, w: s.g.w + ev.clientX - s.x, h: s.g.h + ev.clientY - s.y }));
      window.addEventListener('resize', () => { if (el.classList.contains('floating')) { const g = fclamp(geo()); apply(el, g); el.dataset.geo = JSON.stringify(g); } });
    },
    close(id) {
      const el = document.getElementById(id); if (!el) return;
      ['left', 'top', 'width', 'height', 'right', 'bottom'].forEach(k => el.style.removeProperty(k));
    },
    scrollEnd(id) { const el = document.getElementById(id); if (el) el.scrollTop = el.scrollHeight; }
  };
})();

// ---- optional light theme + phone mode (persisted per browser) ----
(() => {
  const KEY = 'livestudio.prefs.v1';
  const read = () => { try { return JSON.parse(localStorage.getItem(KEY) || '{}') || {}; } catch { return {}; } };
  const apply = p => {
    const h = document.documentElement;
    h.setAttribute('data-theme', p.theme === 'light' ? 'light' : 'dark');
    const phone = p.phone === 'on' || (p.phone !== 'off' && window.matchMedia('(max-width: 860px)').matches);
    h.setAttribute('data-phone', phone ? '1' : '0');
    h.style.colorScheme = p.theme === 'light' ? 'light' : 'dark';
  };
  window.lsPrefs = {
    tzOffset() { return new Date().getTimezoneOffset(); },
    get() { const p = read(); return { theme: p.theme === 'light' ? 'light' : 'dark', phone: p.phone === 'on' ? 'on' : p.phone === 'off' ? 'off' : 'auto' }; },
    set(patch) { const p = { ...read(), ...patch }; try { localStorage.setItem(KEY, JSON.stringify(p)); } catch { } apply(p); return this.get(); }
  };
  apply(read());
  window.matchMedia('(max-width: 860px)').addEventListener?.('change', () => apply(read()));
})();
