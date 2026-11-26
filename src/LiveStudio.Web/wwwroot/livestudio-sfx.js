(() => {
  // Soundboard + media audio bus. Everything is mixed into one MediaStreamTrack that is published to the LiveKit
  // room (so the Egress mixes it into the real RTMP output) and, quietly, monitored on the host's speakers.
  let ctx = null, bus = null, dest = null, monitor = null, monitorLevel = .35;
  const media = new WeakMap();
  const SOUNDS = [
    { key: 'applause', label: 'تشویق', icon: 'bi-hand-thumbs-up' },
    { key: 'drumroll', label: 'طبل', icon: 'bi-record-circle' },
    { key: 'tada', label: 'تادا!', icon: 'bi-stars' },
    { key: 'airhorn', label: 'بوق', icon: 'bi-megaphone' },
    { key: 'ding', label: 'زنگ', icon: 'bi-bell' },
    { key: 'buzzer', label: 'ایراد', icon: 'bi-x-octagon' },
    { key: 'whoosh', label: 'ویژ', icon: 'bi-wind' },
    { key: 'pop', label: 'پاپ', icon: 'bi-balloon' },
    { key: 'sad', label: 'غمگین', icon: 'bi-emoji-frown' },
    { key: 'cheer', label: 'هورا', icon: 'bi-emoji-laughing' }
  ];
  function ensure() {
    if (ctx) { if (ctx.state === 'suspended') ctx.resume().catch(() => {}); return ctx; }
    const AC = window.AudioContext || window.webkitAudioContext; if (!AC) throw new Error('Web Audio در این مرورگر پشتیبانی نمی‌شود.');
    ctx = new AC(); bus = ctx.createGain(); bus.gain.value = 1;
    dest = ctx.createMediaStreamDestination(); bus.connect(dest);
    monitor = ctx.createGain(); monitor.gain.value = monitorLevel; bus.connect(monitor); monitor.connect(ctx.destination);
    ctx.resume().catch(() => {});
    return ctx;
  }
  function noise(c, seconds) { const n = Math.floor(c.sampleRate * seconds), b = c.createBuffer(1, n, c.sampleRate), d = b.getChannelData(0); for (let i = 0; i < n; i++) d[i] = Math.random() * 2 - 1; return b; }
  function tone(c, out, t, freq, dur, type, vol, glideTo) {
    const o = c.createOscillator(), g = c.createGain(); o.type = type; o.frequency.setValueAtTime(freq, t); if (glideTo) o.frequency.exponentialRampToValueAtTime(glideTo, t + dur);
    g.gain.setValueAtTime(0.0001, t); g.gain.exponentialRampToValueAtTime(vol, t + .02); g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
    o.connect(g); g.connect(out); o.start(t); o.stop(t + dur + .05);
  }
  function burst(c, out, t, dur, vol, freq, q) {
    const s = c.createBufferSource(); s.buffer = noise(c, dur + .1); const f = c.createBiquadFilter(); f.type = 'bandpass'; f.frequency.value = freq; f.Q.value = q || .8;
    const g = c.createGain(); g.gain.setValueAtTime(0.0001, t); g.gain.exponentialRampToValueAtTime(vol, t + Math.min(.03, dur / 3)); g.gain.exponentialRampToValueAtTime(0.0001, t + dur);
    s.connect(f); f.connect(g); g.connect(out); s.start(t); s.stop(t + dur + .1);
  }
  const RECIPES = {
    applause(c, o, t) { for (let i = 0; i < 70; i++) burst(c, o, t + Math.random() * 2.6, .05 + Math.random() * .08, .16 + Math.random() * .18, 1800 + Math.random() * 3200, .7); burst(c, o, t, 3, .12, 2500, .4); },
    cheer(c, o, t) { burst(c, o, t, 2.2, .35, 1400, .5); for (let i = 0; i < 40; i++) burst(c, o, t + Math.random() * 2, .06, .15, 2000 + Math.random() * 2500, .8); tone(c, o, t, 500, 1.6, 'sawtooth', .06, 900); },
    drumroll(c, o, t) { for (let i = 0; i < 40; i++) burst(c, o, t + i * .06, .06, .12 + i * .004, 180, 1.2); tone(c, o, t + 2.4, 90, .5, 'sine', .5, 50); burst(c, o, t + 2.4, .4, .5, 6000, .5); },
    tada(c, o, t) { [523, 659, 784].forEach((f, i) => tone(c, o, t + i * .09, f, .5, 'triangle', .25)); [1047, 1319, 1568].forEach(f => tone(c, o, t + .32, f, 1.1, 'triangle', .22)); },
    airhorn(c, o, t) { [440, 554, 659].forEach(f => { tone(c, o, t, f, .9, 'sawtooth', .16); tone(c, o, t + 1.0, f, .6, 'sawtooth', .16); }); },
    ding(c, o, t) { tone(c, o, t, 1318, 1.4, 'sine', .35); tone(c, o, t, 2637, .9, 'sine', .12); },
    buzzer(c, o, t) { tone(c, o, t, 140, .7, 'square', .22); tone(c, o, t, 146, .7, 'square', .18); },
    whoosh(c, o, t) { const s = c.createBufferSource(); s.buffer = noise(c, 1.1); const f = c.createBiquadFilter(); f.type = 'bandpass'; f.Q.value = 1.2; f.frequency.setValueAtTime(300, t); f.frequency.exponentialRampToValueAtTime(5000, t + .8);
      const g = c.createGain(); g.gain.setValueAtTime(.0001, t); g.gain.exponentialRampToValueAtTime(.5, t + .45); g.gain.exponentialRampToValueAtTime(.0001, t + 1); s.connect(f); f.connect(g); g.connect(o); s.start(t); s.stop(t + 1.1); },
    pop(c, o, t) { tone(c, o, t, 900, .12, 'sine', .5, 200); burst(c, o, t, .05, .3, 3000, 1); },
    sad(c, o, t) { [392, 370, 349, 330].forEach((f, i) => tone(c, o, t + i * .42, f, i === 3 ? 1.4 : .45, 'triangle', .28, i === 3 ? f * .8 : null)); }
  };
  window.lsSfx = {
    list: () => SOUNDS,
    ready: () => !!ctx,
    init() { ensure(); return true; },
    getTrack() { ensure(); return dest.stream.getAudioTracks()[0] || null; },
    setMonitor(v) { monitorLevel = Math.max(0, Math.min(1, Number(v) || 0)); if (monitor) monitor.gain.value = monitorLevel; },
    setBus(v) { ensure(); bus.gain.value = Math.max(0, Math.min(1.5, Number(v) || 0)); },
    play(key, volume) {
      const c = ensure(), r = RECIPES[key]; if (!r) throw new Error('صدا پیدا نشد.');
      const out = c.createGain(); out.gain.value = Math.max(0, Math.min(1.5, volume == null ? 1 : Number(volume))); out.connect(bus);
      r(c, out, c.currentTime + .02);
      return true;
    },
    // Routes the sound of a <video> into the bus (published to the stream). The element must not be muted.
    routeMedia(video) {
      const c = ensure(); let node = media.get(video);
      if (!node) { node = c.createMediaElementSource(video); media.set(video, node); node.connect(bus); }
      return true;
    },
    // Local sound check for the lobby: plays a short beep only on the speakers.
    testSpeaker() {
      const c = ensure(); const t = c.currentTime + .02, g = c.createGain(); g.connect(c.destination);
      [660, 880].forEach((f, i) => { const o = c.createOscillator(), e = c.createGain(); o.frequency.value = f; e.gain.setValueAtTime(.0001, t + i * .25); e.gain.exponentialRampToValueAtTime(.25, t + i * .25 + .03); e.gain.exponentialRampToValueAtTime(.0001, t + i * .25 + .22); o.connect(e); e.connect(c.destination); o.start(t + i * .25); o.stop(t + i * .25 + .25); });
      return true;
    }
  };
})();
