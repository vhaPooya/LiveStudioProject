(() => {
  // Team remote control. The host studio (the browser that composes the program) executes commands sent over the
  // LiveKit data channel (topic "ls-ctl"). The sender's role is read from its server-issued identity (team-<role>-...),
  // so a member can never execute more than the role allows.
  const ALLOWED = {
    moderator: ['comment.show', 'comment.hide'],
    editor: ['banner.on', 'banner.off', 'countdown.start', 'countdown.pause', 'countdown.reset', 'template.apply', 'sfx.play'],
    video: ['media.play', 'media.stop'],
    observer: []
  };
  const roleOf = p => (/^team-([a-z]+)-/.exec(p?.identity || '') || [])[1] || null;
  const MODE = 'live';
  let notifier = null, timer = 0, hosting = false;

  const state = () => ({
    media: window.studioCompositor?.currentMedia?.() || null,
    banners: window.studioCompositor?.activeBanners?.(MODE) || []
  });
  const push = () => { if (hosting) window.liveStudio?.broadcastState?.(state()).catch(() => {}); };
  const tell = text => { try { notifier?.invokeMethodAsync('OnTeamEvent', text); } catch (_) { } };

  async function execute(cmd, a) {
    const c = window.studioCompositor;
    switch (cmd) {
      case 'comment.show': c.showComment(MODE, a.author, a.text, a.amount); break;
      case 'comment.hide': c.hideComment(MODE); break;
      case 'banner.on': c.bannerOn(MODE, String(a.key || ''), a); break;
      case 'banner.off': c.bannerOff(MODE, String(a.key || '')); break;
      case 'countdown.start': { const id = c.firstLayerId(MODE, 'countdown'); if (id) { c.startCountdown(MODE, id, a.seconds); } break; }
      case 'countdown.pause': { const id = c.firstLayerId(MODE, 'countdown'); if (id) c.pauseCountdown(MODE, id); break; }
      case 'countdown.reset': { const id = c.firstLayerId(MODE, 'countdown'); if (id) c.resetCountdown(MODE, id); break; }
      case 'template.apply': c.applyTemplate(MODE, String(a.key || ''), true); break;
      case 'sfx.play': window.lsSfx.play(String(a.key || ''), a.volume); break;
      case 'media.play': c.playMedia(MODE, String(a.assetId || ''), String(a.title || '')); break;
      case 'media.stop': c.stopMedia(MODE); break;
      default: throw new Error('دستور ناشناخته');
    }
  }

  window.lsTeam = {
    setNotify(ref) { notifier = ref || null; },
    hostReady() {
      hosting = true; clearInterval(timer); timer = setInterval(push, 5000); push();
    },
    hostStop() { hosting = false; clearInterval(timer); timer = 0; },
    mediaChanged(media, ended) { push(); if (ended) tell('پخش ویدئو به پایان رسید.'); },
    // Called for every message on topic ls-ctl (host: commands, observers: state).
    async onControl(msg, participant) {
      if (!msg || typeof msg.cmd !== 'string') return;
      if (msg.cmd === 'state') { // received by team consoles from the host
        if (!/^host-/.test(participant?.identity || '')) return;
        try { window.dispatchEvent(new CustomEvent('ls-team-state', { detail: msg.args || {} })); notifier?.invokeMethodAsync('OnHostState', JSON.stringify(msg.args || {})); } catch (_) { }
        return;
      }
      if (!hosting) return; // only the host studio executes
      const role = roleOf(participant);
      if (!role || !(ALLOWED[role] || []).includes(msg.cmd)) return;
      try { await execute(msg.cmd, msg.args || {}); tell(`${participant?.name || 'عضو تیم'}: ${msg.cmd}`); push(); }
      catch (e) { tell(`دستور ${msg.cmd} اجرا نشد: ${e?.message || e}`); }
    },
    async send(cmd, args) { await window.liveStudio.sendControl(cmd, args || {}); return true; }
  };

  // ---- Pre-join lobby: camera preview + microphone meter + speaker test (nothing is published) ----
  let lobbyStream = null, lobbyCtx = null, lobbyRaf = 0, lobbyFacing = 'user';
  async function lobbyStop() {
    cancelAnimationFrame(lobbyRaf); lobbyRaf = 0;
    if (lobbyStream) lobbyStream.getTracks().forEach(t => t.stop()); lobbyStream = null;
    if (lobbyCtx) { try { await lobbyCtx.close(); } catch (_) { } lobbyCtx = null; }
    const v = document.getElementById('lobbyVideo'); if (v) v.srcObject = null;
    const m = document.getElementById('lobbyMeter'); if (m) m.style.width = '0%';
  }
  window.lsLobby = {
    async devices() {
      const list = await navigator.mediaDevices.enumerateDevices();
      const map = (k, d) => list.filter(x => x.kind === k).map((x, i) => ({ deviceId: x.deviceId, label: x.label || `${d} ${i + 1}` }));
      return { cameras: map('videoinput', 'دوربین'), microphones: map('audioinput', 'میکروفن'), speakers: map('audiooutput', 'خروجی صدا') };
    },
    // Returns what works so the UI can show a pass/fail checklist.
    async start(cameraId, micId, facing) {
      await lobbyStop();
      const result = { camera: false, microphone: false, secure: window.isSecureContext, error: null, width: 0, height: 0 };
      if (!navigator.mediaDevices?.getUserMedia) { result.error = 'مرورگر دسترسی به دوربین/میکروفن را پشتیبانی نمی‌کند یا صفحه روی HTTPS نیست.'; return result; }
      if (facing) lobbyFacing = facing;
      const video = cameraId ? { deviceId: { exact: cameraId }, width: { ideal: 1280 }, height: { ideal: 720 } } : { facingMode: lobbyFacing, width: { ideal: 1280 }, height: { ideal: 720 } };
      const audio = micId ? { deviceId: { exact: micId }, echoCancellation: true, noiseSuppression: true } : { echoCancellation: true, noiseSuppression: true };
      try { lobbyStream = await navigator.mediaDevices.getUserMedia({ video, audio }); }
      catch (e1) {
        try { lobbyStream = await navigator.mediaDevices.getUserMedia({ video }); result.error = 'دسترسی میکروفن ممکن نشد.'; }
        catch (e2) { try { lobbyStream = await navigator.mediaDevices.getUserMedia({ audio }); result.error = 'دسترسی دوربین ممکن نشد.'; } catch (e3) { result.error = 'دسترسی به دوربین و میکروفن داده نشد: ' + (e3?.name || ''); return result; } }
      }
      const vt = lobbyStream.getVideoTracks()[0], at = lobbyStream.getAudioTracks()[0];
      if (vt) { result.camera = true; const st = vt.getSettings?.() || {}; result.width = st.width || 0; result.height = st.height || 0; const v = document.getElementById('lobbyVideo'); if (v) { v.srcObject = new MediaStream([vt]); v.muted = true; v.playsInline = true; v.play().catch(() => {}); } }
      if (at) {
        result.microphone = true;
        const AC = window.AudioContext || window.webkitAudioContext; lobbyCtx = new AC(); await lobbyCtx.resume();
        const src = lobbyCtx.createMediaStreamSource(new MediaStream([at])), an = lobbyCtx.createAnalyser(); an.fftSize = 512; src.connect(an);
        const data = new Uint8Array(an.fftSize);
        const tick = () => { if (!lobbyCtx) return; an.getByteTimeDomainData(data); let sum = 0; for (const x of data) { const n = (x - 128) / 128; sum += n * n; } const pct = Math.min(100, Math.round(Math.sqrt(sum / data.length) * 300)); const m = document.getElementById('lobbyMeter'); if (m) m.style.width = pct + '%'; lobbyRaf = requestAnimationFrame(tick); };
        tick();
      }
      return result;
    },
    stop: lobbyStop,
    speakerTest() { window.lsSfx.testSpeaker(); return true; }
  };
})();
