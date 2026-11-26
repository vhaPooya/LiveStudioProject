(() => {
  const STORAGE = 'livestudio.compositor.v1';
  const W = 1920, H = 1080;
  const defaults = () => ({
    backgroundId: null, camera: { brightness: 100, contrast: 100, saturation: 100, exposure: 0, hue: 0, chromaEnabled: false, chromaColor: '#00ff00', chromaTolerance: 90, chromaSoftness: 35 },
    cameraFrame: { x: 0, y: 0, w: 100, h: 100, fit: 'cover', layout: 'full' },
    layers: []
  });
  const clamp = (v, min, max, fallback = min) => Number.isFinite(Number(v)) ? Math.min(max, Math.max(min, Number(v))) : fallback;
  const stateByMode = new Map();
  const programStateByMode = new Map();
  const instances = new Map();
  const assetsCache = new Map();
  let assetList = [];
  let keyCanvas = null, keyContext = null;
  const readState = mode => {
    if (stateByMode.has(mode)) return stateByMode.get(mode);
    let value;
    try { value = JSON.parse(localStorage.getItem(`${STORAGE}.${mode}`) || 'null'); } catch { value = null; }
    const base = defaults();
    const state = { ...base, ...(value || {}), camera: { ...base.camera, ...(value?.camera || {}) }, cameraFrame: { ...base.cameraFrame, ...(value?.cameraFrame || {}) }, layers: Array.isArray(value?.layers) ? value.layers : [] };
    state.layers.forEach(layer => { normAnim(layer); delete layer.exitAt; layer.startedAt = performance.now(); layer.color = { brightness:100, contrast:100, saturation:100, exposure:0, hue:0, ...(layer.color || {}) }; });
    stateByMode.set(mode, state); return state;
  };
  const save = mode => localStorage.setItem(`${STORAGE}.${mode}`, JSON.stringify(readState(mode)));
  const readProgramState = mode => { if(programStateByMode.has(mode)) return programStateByMode.get(mode); let value; try{value=JSON.parse(localStorage.getItem(`${STORAGE}.${mode}.program`)||'null');}catch{value=null;} const state=value?{...value,camera:{...defaults().camera,...(value.camera||{})},cameraFrame:{...defaults().cameraFrame,...(value.cameraFrame||{})},layers:Array.isArray(value.layers)?value.layers:[]}:JSON.parse(JSON.stringify(readState(mode))); state.layers.forEach(layer=>{normAnim(layer);delete layer.exitAt;layer.startedAt=performance.now();layer.color={brightness:100,contrast:100,saturation:100,exposure:0,hue:0,...(layer.color||{})};}); programStateByMode.set(mode,state); return state; };
  const saveProgram = mode => localStorage.setItem(`${STORAGE}.${mode}.program`,JSON.stringify(readProgramState(mode)));
  const id = () => `layer-${crypto.randomUUID()}`;
  const assetById = assetId => assetList.find(x => x.id === assetId);
  function loadAsset(a) {
    return new Promise(resolve => {
      const done = () => resolve();
      if (a.mimeType?.startsWith('image/')) {
        const img = new Image(); img.onload = done; img.onerror = done; img.src = a.url; assetsCache.set(a.id, img);
      } else if (a.mimeType?.startsWith('video/')) {
        const video = document.createElement('video'); video.loop = true; video.muted = true; video.playsInline = true; video.preload = 'auto';
        video.onloadeddata = done; video.onerror = done; video.src = a.url; assetsCache.set(a.id, video);
        const p = video.play(); if (p?.catch) p.catch(() => {});
        setTimeout(done, 2500);
      } else resolve();
    });
  }
  async function refreshAssets() {
    for (const item of assetsCache.values()) if (item instanceof HTMLVideoElement) { try { item.pause(); item.removeAttribute('src'); item.load(); } catch (_) {} }
    assetsCache.clear();
    assetList = await (window.mediaBox?.list?.() || []);
    await Promise.race([Promise.allSettled(assetList.map(loadAsset)), new Promise(r => setTimeout(r, 4000))]);
    return assetList;
  }
  const sourceSize = source => {
    let w = source?.videoWidth || source?.naturalWidth || 0, h = source?.videoHeight || source?.naturalHeight || 0;
    if ((!w || !h) && source instanceof HTMLImageElement && source.complete && source.src) { w = source.width || 800; h = source.height || 450; }
    if ((!w || !h) && source instanceof HTMLCanvasElement) { w = source.width; h = source.height; }
    return { w, h };
  };
  function drawCover(ctx, source, x, y, w, h, fit = 'cover') {
    const { w: sw, h: sh } = sourceSize(source); if (!sw || !sh) return;
    const scale = fit === 'contain' ? Math.min(w / sw, h / sh) : Math.max(w / sw, h / sh);
    const dw = sw * scale, dh = sh * scale;
    ctx.drawImage(source, x + (w - dw) / 2, y + (h - dh) / 2, dw, dh);
  }
  function colorFilter(c) { return `brightness(${clamp(c.brightness, 0, 250, 100) * Math.pow(2, clamp(c.exposure, -100, 100, 0) / 100)}%) contrast(${clamp(c.contrast, 0, 300, 100)}%) saturate(${clamp(c.saturation, 0, 300, 100)}%) hue-rotate(${clamp(c.hue, -180, 180, 0)}deg)`; }
  function chroma(ctx, x, y, w, h, c) {
    if (!c.chromaEnabled || w > 1280 || h > 720) return;
    try {
      const frame = ctx.getImageData(x, y, w, h), d = frame.data;
      const hex = c.chromaColor || '#00ff00', key = [1,3,5].map(i => parseInt(hex.slice(i, i+2),16));
      const threshold = clamp(c.chromaTolerance, 5, 240, 90), softness = Math.max(1, clamp(c.chromaSoftness, 0, 150, 35));
      for (let i=0;i<d.length;i+=4) { const dr=d[i]-key[0],dg=d[i+1]-key[1],db=d[i+2]-key[2]; const dist=Math.sqrt(dr*dr+dg*dg+db*db); d[i+3]=Math.round(d[i+3]*Math.max(0,Math.min(1,(dist-threshold)/softness))); }
      ctx.putImageData(frame,x,y);
    } catch (e) { console.warn('Chroma key skipped',e); }
  }
  // ---------------------------------------------------------------- Animation engine
  const EASE = {
    linear: t => t,
    easeOut: t => 1 - Math.pow(1 - t, 3),
    easeIn: t => t * t * t,
    easeInOut: t => t < .5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2,
    back: t => { const c1 = 1.70158, c3 = c1 + 1; return 1 + c3 * Math.pow(t - 1, 3) + c1 * Math.pow(t - 1, 2); },
    elastic: t => t === 0 ? 0 : t === 1 ? 1 : Math.pow(2, -10 * t) * Math.sin((t * 10 - .75) * (2 * Math.PI) / 3) + 1,
    bounce: t => { const n = 7.5625, d = 2.75; if (t < 1 / d) return n * t * t; if (t < 2 / d) return n * (t -= 1.5 / d) * t + .75; if (t < 2.5 / d) return n * (t -= 2.25 / d) * t + .9375; return n * (t -= 2.625 / d) * t + .984375; }
  };
  // "from" state of every entrance preset (an exit plays the same state in reverse). dx/dy are multiples of the travel distance.
  const IN_PRESETS = {
    'fade': { ease: 'easeOut', a: 0 },
    'slide-left': { ease: 'easeOut', a: 0, dx: -1 },
    'slide-right': { ease: 'easeOut', a: 0, dx: 1 },
    'slide-up': { ease: 'easeOut', a: 0, dy: 1 },
    'slide-down': { ease: 'easeOut', a: 0, dy: -1 },
    'zoom-in': { ease: 'easeOut', a: 0, s: .35 },
    'zoom-out': { ease: 'easeOut', a: 0, s: 1.9 },
    'pop': { ease: 'back', a: 0, s: 0 },
    'elastic': { ease: 'elastic', a: 0, s: 0 },
    'bounce-in': { ease: 'bounce', a: 0, dy: -1.6 },
    'drop': { ease: 'bounce', dy: -3 },
    'spin-in': { ease: 'easeOut', a: 0, s: .2, r: -Math.PI * 2 },
    'flip-x': { ease: 'back', sx: 0 },
    'flip-y': { ease: 'back', sy: 0 },
    'blur-in': { ease: 'easeOut', a: 0, b: 26 },
    'reveal-left': { ease: 'easeInOut', clip: 'l' },
    'reveal-right': { ease: 'easeInOut', clip: 'r' },
    'reveal-up': { ease: 'easeInOut', clip: 'u' },
    'reveal-down': { ease: 'easeInOut', clip: 'd' },
    'glitch': { ease: 'linear', glitch: true }
  };

  const NOASSET = ['text', 'lowerThird', 'breaking', 'ticker', 'countdown', 'comment'];
  const TYPE_DEFAULTS = {
    ticker: { x: 0, y: 90, w: 100, h: 9, text: 'تیتر اول خبر\nتیتر دوم خبر\nتیتر سوم خبر', tag: 'خبر', backgroundColor: '#0b1b33', accentColor: '#e5384f', textColor: '#ffffff', fontSize: 34, tickerSpeed: 140, tickerDir: 'left' },
    countdown: { x: 30, y: 26, w: 40, h: 32, text: 'به‌زودی شروع می‌شود', cdTotal: 300, pausedLeft: 300000, doneText: 'شروع شد!', backgroundColor: 'rgba(7,16,30,.92)', accentColor: '#3b78ff', textColor: '#ffffff', fontSize: 110, cdAutoHide: false },
    comment: { x: 4, y: 68, w: 58, h: 22, author: '', text: '', amount: '', backgroundColor: 'rgba(255,255,255,.96)', textColor: '#10172a', accentColor: '#3b78ff', fontSize: 34, animIn: 'slide-up', animInDur: .5, animOut: 'fade', animOutDur: .4 }
  };
  const BANNERS = {
    'lt-classic': { type: 'lowerThird', ltStyle: 'classic', title: 'نام مهمان', subtitle: 'عنوان یا سمت', x: 5, y: 76, w: 46, h: 14 },
    'lt-glass': { type: 'lowerThird', ltStyle: 'glass', title: 'نام مهمان', subtitle: 'عنوان یا سمت', x: 5, y: 76, w: 46, h: 14, accentColor: '#4cc9f0' },
    'lt-bar': { type: 'lowerThird', ltStyle: 'bar', title: 'نام مهمان', subtitle: 'عنوان یا سمت', x: 5, y: 76, w: 46, h: 14, accentColor: '#3b78ff' },
    'lt-minimal': { type: 'lowerThird', ltStyle: 'minimal', title: 'نام مهمان', subtitle: 'عنوان یا سمت', x: 5, y: 76, w: 46, h: 14 },
    'subscribe': { type: 'lowerThird', ltStyle: 'bar', title: 'لایک و سابسکرایب فراموش نشه!', subtitle: '', x: 5, y: 82, w: 44, h: 9, accentColor: '#e5384f', fontSize: 34 },
    'welcome': { type: 'lowerThird', ltStyle: 'bar', title: 'خوش آمدید 👋', subtitle: '', x: 5, y: 82, w: 34, h: 9, accentColor: '#ff7a3d', fontSize: 38 },
    'qa': { type: 'lowerThird', ltStyle: 'glass', title: 'پرسش و پاسخ', subtitle: 'سؤال خود را در چت بنویسید', x: 5, y: 76, w: 40, h: 14, accentColor: '#9b6bff' },
    'achievement': { type: 'lowerThird', ltStyle: 'bar', title: '🏆 دستاورد جدید', subtitle: 'متن دستاورد', x: 5, y: 76, w: 40, h: 14, accentColor: '#f5a623' },
    'breaking': { type: 'breaking', text: 'تیتر خبر فوری', x: 0, y: 88, w: 100, h: 12 },
    'ticker': { type: 'ticker' }
  };
  const ANIM_KEYS = ['animIn', 'animInDur', 'animInDelay', 'animInEase', 'animDistance', 'animLoop', 'animLoopSpeed', 'animLoopIntensity', 'animOut', 'animOutDur', 'animCycle', 'animHold', 'animGap', 'glowColor'];
  // Applies the same change to the Preview state and the live Program state (overlays go on air at once).
  function both(mode, fn) { fn(readState(mode)); save(mode); if (mode === 'live') { fn(readProgramState(mode)); saveProgram(mode); } }

  // ---- layer factory, templates, keyed banners and media playback (remote-controllable) ----
  const ZERO_COLOR = () => ({ brightness: 100, contrast: 100, saturation: 100, exposure: 0, hue: 0 });
  function newLayer(spec, z) {
    const base = { id: id(), type: spec.type || 'text', assetId: null, text: '', title: '', subtitle: '', x: 8, y: 8, w: 24, h: 18, opacity: 100, visible: true, locked: false, z, startedAt: performance.now(),
      fontSize: 40, textColor: '#ffffff', backgroundColor: 'rgba(7,16,30,.88)', accentColor: '#6d7cff', color: ZERO_COLOR(), animIn: 'none', animInDur: .55, animDistance: 18, animOut: 'none', animOutDur: .45, animLoop: 'none' };
    return Object.assign(base, JSON.parse(JSON.stringify(TYPE_DEFAULTS[spec.type] || {})), JSON.parse(JSON.stringify(spec)), { id: base.id });
  }
  const TEMPLATES = [
    { key: 'interview', label: 'مصاحبه', icon: 'bi-people', bg: ['#2a1b4d', '#0b0816'], camera: { x: 3, y: 14, w: 58, h: 72, fit: 'cover', layout: 'left' },
      layers: [ { type: 'text', text: 'عنوان گفتگو', x: 64, y: 14, w: 33, h: 14, fontSize: 52, textColor: '#ffffff', backgroundColor: 'rgba(109,124,255,.28)' },
                { type: 'text', text: 'مهمان این قسمت', x: 64, y: 32, w: 33, h: 10, fontSize: 34, textColor: '#c9d2ff', backgroundColor: 'rgba(255,255,255,.08)' },
                { type: 'lowerThird', ltStyle: 'glass', title: 'نام میزبان', subtitle: 'عنوان', x: 3, y: 78, w: 40, h: 14 } ] },
    { key: 'news', label: 'خبری', icon: 'bi-newspaper', bg: ['#0b2447', '#050b16'], camera: { x: 0, y: 0, w: 100, h: 100, fit: 'cover', layout: 'full' },
      layers: [ { type: 'lowerThird', ltStyle: 'bar', title: 'گزارشگر', subtitle: 'شبکه خبر', x: 4, y: 70, w: 46, h: 14, accentColor: '#e5384f' },
                { type: 'ticker', text: 'تیتر اول خبر\nتیتر دوم خبر\nتیتر سوم خبر', tag: 'خبر', x: 0, y: 90, w: 100, h: 9 } ] },
    { key: 'presentation', label: 'ارائه', icon: 'bi-easel', bg: ['#0f2b3d', '#06121b'], camera: { x: 72, y: 62, w: 25, h: 33, fit: 'cover', layout: 'pip' },
      layers: [ { type: 'text', text: 'عنوان ارائه', x: 5, y: 8, w: 90, h: 16, fontSize: 70, textColor: '#ffffff', backgroundColor: 'rgba(0,0,0,0)' },
                { type: 'text', text: 'موضوع اول\nموضوع دوم\nموضوع سوم', x: 5, y: 28, w: 62, h: 50, fontSize: 44, textColor: '#d8e6ff', backgroundColor: 'rgba(255,255,255,.07)' } ] },
    { key: 'gaming', label: 'گیمینگ', icon: 'bi-controller', bg: ['#2b0a4a', '#04101f'], camera: { x: 70, y: 60, w: 28, h: 36, fit: 'cover', layout: 'pip' },
      layers: [ { type: 'lowerThird', ltStyle: 'bar', title: 'لایک و سابسکرایب فراموش نشه!', subtitle: '', x: 4, y: 86, w: 44, h: 9, accentColor: '#7c3aed', fontSize: 32 } ] },
    { key: 'podcast', label: 'پادکست', icon: 'bi-mic', bg: ['#2d1608', '#0b0604'], camera: { x: 20, y: 20, w: 60, h: 62, fit: 'cover', layout: 'pip' },
      layers: [ { type: 'text', text: 'نام پادکست', x: 20, y: 5, w: 60, h: 11, fontSize: 56, textColor: '#ffd9a8', backgroundColor: 'rgba(0,0,0,0)' },
                { type: 'lowerThird', ltStyle: 'minimal', title: 'نام میزبان', subtitle: 'قسمت ۱', x: 5, y: 84, w: 40, h: 11 } ] },
    { key: 'starting', label: 'به‌زودی شروع می‌شود', icon: 'bi-hourglass-split', bg: ['#10264a', '#040912'], camera: { x: 72, y: 66, w: 24, h: 28, fit: 'cover', layout: 'pip' },
      layers: [ { type: 'countdown', text: 'پخش زنده شروع می‌شود', cdTotal: 300, pausedLeft: 300000, x: 25, y: 22, w: 50, h: 38 },
                { type: 'text', text: 'لطفاً منتظر بمانید…', x: 25, y: 66, w: 50, h: 9, fontSize: 36, textColor: '#9fb4d8', backgroundColor: 'rgba(0,0,0,0)' } ] }
  ];
  const pick = (o, keys) => { const r = {}; for (const k of keys) if (o && o[k] !== undefined && o[k] !== null) r[k] = String(o[k]).slice(0, 200); return r; };
  function removeMediaLayers(st) { st.layers = st.layers.filter(l => !l.mediaPlay); }
  let mediaPlaying = null;
  const hash = n => { const v = Math.sin(n * 127.1 + 311.7) * 43758.5453; return v - Math.floor(v); };
  const num = (v, min, max, fb) => clamp(v, min, max, fb);
  const ANIM_DEFAULTS = { animIn: 'none', animInDur: .8, animInDelay: 0, animInEase: 'auto', animDistance: 15, animLoop: 'none', animLoopSpeed: 1, animLoopIntensity: 100, animOut: 'none', animOutDur: .5, animCycle: 'none', animHold: 4, animGap: 2 };
  const LEGACY_IN = { fade: 'fade', 'slide-left': 'slide-left', 'slide-right': 'slide-right', zoom: 'zoom-in' };
  const LEGACY_LOOP = { pulse: 'pulse', float: 'float', bounce: 'bounce', rotate: 'sway', glow: 'glow' };
  function normAnim(layer) {
    if (!layer) return layer;
    const legacy = layer.animation;
    for (const k of Object.keys(ANIM_DEFAULTS)) if (layer[k] === undefined) layer[k] = ANIM_DEFAULTS[k];
    if (legacy !== undefined) {
      if (LEGACY_IN[legacy]) layer.animIn = LEGACY_IN[legacy];
      if (LEGACY_LOOP[legacy]) layer.animLoop = LEGACY_LOOP[legacy];
      delete layer.animation;
    }
    return layer;
  }
  function exitActive(layer, now) {
    if (layer.visible !== false || !layer.exitAt || (layer.animOut || 'none') === 'none' || !IN_PRESETS[layer.animOut]) return false;
    const q = (now - layer.exitAt) / 1000 / num(layer.animOutDur, .1, 3, .5);
    return q >= 0 && q < 1;
  }
  function animation(layer, now) {
    normAnim(layer);
    const op = clamp(layer.opacity, 0, 100, 100) / 100, W_ = W, H_ = H;
    const o = { alpha: op, sx: 1, sy: 1, dx: 0, dy: 0, rot: 0, blur: 0, clip: null, glow: null };
    const dist = num(layer.animDistance, 2, 80, 15) / 100;
    const apply = (from, k, p) => {
      if (from.a !== undefined) o.alpha *= Math.max(0, Math.min(1, from.a + (1 - from.a) * (1 - k)));
      if (from.s !== undefined) { const m = 1 + (from.s - 1) * k; o.sx *= m; o.sy *= m; }
      if (from.sx !== undefined) o.sx *= 1 + (from.sx - 1) * k;
      if (from.sy !== undefined) o.sy *= 1 + (from.sy - 1) * k;
      if (from.dx) o.dx += from.dx * k * dist * W_;
      if (from.dy) o.dy += from.dy * k * dist * H_;
      if (from.r) o.rot += from.r * k;
      if (from.b) o.blur += from.b * Math.max(0, k);
      if (from.clip) o.clip = { dir: from.clip, p: Math.max(0, Math.min(1, 1 - k)) };
      if (from.glitch) { const f = Math.floor(p * 28); o.alpha *= hash(f) > .42 ? 1 : .12; o.dx += (hash(f + 9) - .5) * 70 * Math.max(0, k); o.dy += (hash(f + 21) - .5) * 14 * Math.max(0, k); }
    };
    if (layer.visible === false) {
      const preset = IN_PRESETS[layer.animOut], q = Math.max(0, Math.min(1, (now - layer.exitAt) / 1000 / num(layer.animOutDur, .1, 3, .5)));
      apply(preset, EASE.easeIn(q), q);
      return o;
    }
    let t = (now - (layer.startedAt || 0)) / 1000;
    const dur = num(layer.animInDur, .1, 5, .8), delay = num(layer.animInDelay, 0, 10, 0), preset = IN_PRESETS[layer.animIn];
    const hasIn = !!(preset && layer.animIn !== 'none'), enter = hasIn ? delay + dur : 0;
    if ((layer.animCycle || 'none') === 'repeat') {
      // Auto cycle: entrance -> hold (loop plays) -> exit -> hidden gap -> entrance ...
      const hold = num(layer.animHold, 0, 120, 4), gap = num(layer.animGap, 0, 120, 2);
      const outP = layer.animOut && layer.animOut !== 'none' ? IN_PRESETS[layer.animOut] : null;
      const outDur = outP ? num(layer.animOutDur, .1, 3, .5) : 0;
      const period = Math.max(.4, enter + hold + outDur + gap);
      t = ((t % period) + period) % period;
      if (t >= enter + hold) {
        if (outP && t < enter + hold + outDur) { const q = (t - enter - hold) / outDur; apply(outP, EASE.easeIn(q), q); }
        else o.alpha = 0;
        return o;
      }
    }
    if (hasIn) {
      if (t < delay) apply(preset, 1, 0);
      else if (t < delay + dur) { const p = (t - delay) / dur, easing = EASE[layer.animInEase !== 'auto' && EASE[layer.animInEase] ? layer.animInEase : preset.ease] || EASE.easeOut; apply(preset, 1 - easing(p), p); }
    }
    const loop = layer.animLoop || 'none';
    if (loop !== 'none') {
      const lt = Math.max(0, t - enter), sp = num(layer.animLoopSpeed, .1, 5, 1), k = num(layer.animLoopIntensity, 0, 300, 100) / 100;
      switch (loop) {
        case 'pulse': { const m = 1 + .045 * k * Math.sin(lt * 3.2 * sp); o.sx *= m; o.sy *= m; break; }
        case 'heartbeat': { const ph = (lt * sp * 1.1) % 1, beat = Math.exp(-Math.pow((ph - .12) * 14, 2)) + .7 * Math.exp(-Math.pow((ph - .3) * 14, 2)); const m = 1 + .09 * k * beat; o.sx *= m; o.sy *= m; break; }
        case 'breathe': { const m = 1 + .03 * k * Math.sin(lt * 1.6 * sp); o.sx *= m; o.sy *= m; o.alpha *= 1 - .25 * Math.min(1, k) * (.5 + .5 * Math.sin(lt * 1.6 * sp)); break; }
        case 'float': o.dy += Math.sin(lt * 2.1 * sp) * 10 * k; break;
        case 'drift': o.dx += Math.sin(lt * 1.3 * sp) * 14 * k; o.dy += Math.cos(lt * 1.9 * sp) * 8 * k; break;
        case 'bounce': o.dy += Math.abs(Math.sin(lt * 2.5 * sp)) * -18 * k; break;
        case 'sway': o.rot += Math.sin(lt * 1.6 * sp) * .08 * k; break;
        case 'spin': o.rot += lt * sp * 1.2; break;
        case 'wobble': o.rot += Math.sin(lt * 5 * sp) * .05 * k; o.dx += Math.sin(lt * 5 * sp) * 6 * k; break;
        case 'shake': { const ph = (lt * sp) % 3; if (ph < .5) { const amp = (1 - ph / .5) * 8 * k; o.dx += Math.sin(lt * 60) * amp; o.dy += Math.cos(lt * 53) * amp * .6; } break; }
        case 'flicker': { const f = Math.floor(lt * 14 * sp); o.alpha *= 1 - .6 * Math.min(1, k) * (hash(f) > .78 ? 1 : 0); break; }
        case 'blink': { const off = Math.floor(lt * sp * 2) % 2 === 1; if (off) o.alpha *= 1 - Math.min(1, k); break; }
        case 'fade-pulse': o.alpha *= 1 - Math.min(1, k) * (.5 - .5 * Math.cos(lt * 2.4 * sp)); break;
        case 'glow': o.glow = { color: layer.glowColor || '#ffffff', blur: Math.max(0, (22 + Math.sin(lt * 4 * sp) * 8) * k) }; break;
      }
    }
    return o;
  }
  const ANIM_PRESETS = {
    none: {},
    'clean-fade': { animIn: 'fade', animInDur: .9, animOut: 'fade', animOutDur: .6 },
    'logo-pop': { animIn: 'pop', animInDur: .7, animLoop: 'heartbeat', animLoopSpeed: .6, animLoopIntensity: 60, animOut: 'fade', animOutDur: .4 },
    'logo-cinematic': { animIn: 'blur-in', animInDur: 1.4, animLoop: 'float', animLoopSpeed: .5, animLoopIntensity: 50, animOut: 'blur-in', animOutDur: .8 },
    'logo-neon': { animIn: 'glitch', animInDur: .7, animLoop: 'glow', animLoopSpeed: .8, animLoopIntensity: 130, glowColor: '#7c8cff', animOut: 'fade', animOutDur: .4 },
    'logo-bounce': { animIn: 'drop', animInDur: 1.1, animDistance: 25, animLoop: 'pulse', animLoopIntensity: 55, animOut: 'slide-up', animOutDur: .5 },
    'logo-elastic': { animIn: 'elastic', animInDur: 1.0, animLoop: 'sway', animLoopSpeed: .7, animLoopIntensity: 50, animOut: 'zoom-in', animOutDur: .5 },
    'logo-spin': { animIn: 'spin-in', animInDur: .95, animLoop: 'glow', animLoopSpeed: .7, animLoopIntensity: 80, glowColor: '#ffffff', animOut: 'spin-in', animOutDur: .6 },
    'logo-wipe': { animIn: 'reveal-left', animInDur: .8, animLoop: 'breathe', animLoopSpeed: .6, animLoopIntensity: 50, animOut: 'reveal-left', animOutDur: .6 },
    'logo-blink': { animIn: 'fade', animInDur: .5, animLoop: 'blink', animLoopSpeed: .8, animLoopIntensity: 100, animOut: 'fade', animOutDur: .4 },
    'logo-fade-loop': { animIn: 'fade', animInDur: .8, animLoop: 'fade-pulse', animLoopSpeed: .6, animLoopIntensity: 80, animOut: 'fade', animOutDur: .6 },
    'logo-auto-cycle': { animIn: 'zoom-out', animInDur: .7, animOut: 'fade', animOutDur: .6, animCycle: 'repeat', animHold: 6, animGap: 4 },
    'logo-reveal-cycle': { animIn: 'reveal-left', animInDur: .8, animLoop: 'glow', animLoopIntensity: 60, animOut: 'reveal-left', animOutDur: .6, animCycle: 'repeat', animHold: 5, animGap: 3 },
    'news-slide': { animIn: 'slide-right', animInDur: .6, animDistance: 30, animOut: 'slide-right', animOutDur: .45 },
    'corner-tag': { animIn: 'slide-down', animInDur: .55, animDistance: 20, animLoop: 'breathe', animLoopSpeed: .5, animLoopIntensity: 40, animOut: 'slide-down', animOutDur: .4 },
    'attention': { animIn: 'zoom-out', animInDur: .5, animLoop: 'shake', animLoopSpeed: 1.2, animLoopIntensity: 100, animOut: 'fade', animOutDur: .3 }
  };

  // ---- rounded rect / text wrap / new overlay layers (ticker, countdown, comment) ----
  function rr(ctx, x, y, w, h, r) { r = Math.min(r, w / 2, h / 2); ctx.beginPath(); ctx.moveTo(x + r, y); ctx.arcTo(x + w, y, x + w, y + h, r); ctx.arcTo(x + w, y + h, x, y + h, r); ctx.arcTo(x, y + h, x, y, r); ctx.arcTo(x, y, x + w, y, r); ctx.closePath(); }
  function wrapLines(ctx, text, maxW, maxLines) {
    const words = String(text || '').split(/\s+/).filter(Boolean), lines = []; let cur = '';
    for (const w of words) { const t = cur ? cur + ' ' + w : w; if (ctx.measureText(t).width > maxW && cur) { lines.push(cur); cur = w; } else cur = t; }
    if (cur) lines.push(cur);
    if (lines.length > maxLines) { lines.length = maxLines; lines[maxLines - 1] = lines[maxLines - 1].replace(/\s*\S*$/, '') + '…'; }
    return lines;
  }
  function cdRemaining(l) { return Math.max(0, l.endAt ? l.endAt - Date.now() : (l.pausedLeft ?? (l.cdTotal || 300) * 1000)); }
  function fmtClock(ms) {
    const t = Math.ceil(ms / 1000), h = Math.floor(t / 3600), m = Math.floor((t % 3600) / 60), s = t % 60, z = n => String(n).padStart(2, '0');
    return h > 0 ? `${h}:${z(m)}:${z(s)}` : `${z(m)}:${z(s)}`;
  }
  function drawSpecial(ctx, layer, w, h, now, blurStr) {
    ctx.filter = colorFilter(layer.color || {}) + blurStr;
    if (layer.type === 'ticker') {
      const tagW = layer.tag ? Math.min(w * .16, 220) : 0, fs = clamp(layer.fontSize, 16, 90, 34);
      ctx.fillStyle = layer.backgroundColor || '#0b1b33'; ctx.fillRect(-w / 2, -h / 2, w, h);
      ctx.save(); ctx.beginPath(); ctx.rect(-w / 2, -h / 2, w - tagW, h); ctx.clip();
      ctx.direction = 'rtl'; ctx.textBaseline = 'middle'; ctx.font = `700 ${fs}px Arial`; ctx.fillStyle = layer.textColor || '#fff';
      const items = String(layer.text || '').split('\n').map(x => x.trim()).filter(Boolean), sep = '   ◆   ';
      const line = (items.length ? items.join(sep) : '—') + sep, tw = Math.max(40, ctx.measureText(line).width);
      const speed = clamp(layer.tickerSpeed, 30, 600, 140), dirLeft = (layer.tickerDir || 'left') === 'left';
      const off = ((now / 1000) * speed) % tw;
      ctx.textAlign = 'left';
      for (let k = -1; k * tw < w + tw; k++) { const x0 = dirLeft ? -w / 2 - off + k * tw : -w / 2 + off + (k - 1) * tw; ctx.fillText(line, x0, 2); }
      ctx.restore();
      if (tagW) { ctx.fillStyle = layer.accentColor || '#e5384f'; ctx.fillRect(w / 2 - tagW, -h / 2, tagW, h); ctx.fillStyle = '#fff'; ctx.textAlign = 'center'; ctx.direction = 'rtl'; ctx.font = `800 ${fs * .8}px Arial`; ctx.fillText(layer.tag, w / 2 - tagW / 2, 2, tagW - 16); }
      return true;
    }
    if (layer.type === 'countdown') {
      const left = cdRemaining(layer), done = left <= 0 && layer.endAt;
      if (done && layer.cdAutoHide && Date.now() - layer.endAt > 3000) return true;
      rr(ctx, -w / 2, -h / 2, w, h, Math.min(w, h) * .09); ctx.fillStyle = layer.backgroundColor || 'rgba(7,16,30,.9)'; ctx.fill();
      ctx.lineWidth = Math.max(3, h * .018); ctx.strokeStyle = layer.accentColor || '#3b78ff'; ctx.stroke();
      ctx.textAlign = 'center'; ctx.textBaseline = 'middle'; ctx.direction = 'rtl'; ctx.fillStyle = layer.textColor || '#fff';
      const label = done ? (layer.doneText || 'شروع شد!') : (layer.text || '');
      if (label) { ctx.font = `700 ${Math.max(18, h * .15)}px Arial`; ctx.fillStyle = layer.accentColor || '#3b78ff'; ctx.fillText(label, 0, -h * .27, w * .9); ctx.fillStyle = layer.textColor || '#fff'; }
      if (!done) { ctx.direction = 'ltr'; ctx.font = `800 ${Math.min(clamp(layer.fontSize, 20, 300, 96), h * .5)}px Arial`; ctx.fillText(fmtClock(left), 0, h * .1, w * .92); }
      return true;
    }
    if (layer.type === 'comment') {
      rr(ctx, -w / 2, -h / 2, w, h, h * .22); ctx.fillStyle = layer.backgroundColor || 'rgba(255,255,255,.96)'; ctx.shadowColor = 'rgba(0,0,0,.45)'; ctx.shadowBlur = 22; ctx.fill(); ctx.shadowBlur = 0;
      const r = h * .26, cx = w / 2 - r - h * .12, cy = -h / 2 + h * .12 + r;
      ctx.fillStyle = layer.accentColor || '#3b78ff'; ctx.beginPath(); ctx.arc(cx, cy, r, 0, Math.PI * 2); ctx.fill();
      const initial = (String(layer.author || '؟').trim()[0] || '؟').toUpperCase();
      ctx.fillStyle = '#fff'; ctx.textAlign = 'center'; ctx.textBaseline = 'middle'; ctx.direction = 'rtl'; ctx.font = `800 ${r * 1.1}px Arial`; ctx.fillText(initial, cx, cy + 1);
      const tx = cx - r - h * .1, maxW = w - (w / 2 - tx) - h * .18 - w * .02 + w / 2 - w / 2;
      ctx.textAlign = 'right'; ctx.fillStyle = layer.accentColor || '#3b78ff'; ctx.font = `800 ${h * .17}px Arial`;
      const head = (layer.author || '') + (layer.amount ? `  •  ${layer.amount}` : ''); ctx.fillText(head, tx, -h / 2 + h * .2, tx + w / 2 - h * .15);
      ctx.fillStyle = layer.textColor || '#10172a'; const fs = clamp(layer.fontSize, 16, 80, 32); ctx.font = `600 ${fs}px Arial`;
      const lines = wrapLines(ctx, layer.text, tx + w / 2 - h * .15, Math.max(1, Math.floor((h * .66) / (fs * 1.35))));
      lines.forEach((ln, i) => ctx.fillText(ln, tx, -h / 2 + h * .42 + i * fs * 1.35, tx + w / 2 - h * .15));
      return true;
    }
    return false;
  }
  const LT_STYLES = {
    glass(ctx, layer, w, h, title, sub) { rr(ctx, -w / 2, -h / 2, w, h, h * .28); ctx.fillStyle = layer.backgroundColor && !/^rgba\(7,16,30/.test(layer.backgroundColor) ? layer.backgroundColor : 'rgba(18,28,48,.62)'; ctx.fill(); ctx.lineWidth = 2; ctx.strokeStyle = 'rgba(255,255,255,.28)'; ctx.stroke(); ctx.fillStyle = layer.accentColor || '#6d7cff'; rr(ctx, w / 2 - 14, -h / 2 + h * .2, 6, h * .6, 3); ctx.fill(); LT_TEXT(ctx, layer, w, h, title, sub, w / 2 - 34); },
    bar(ctx, layer, w, h, title, sub) { const top = h * (sub ? .58 : 1); ctx.fillStyle = layer.accentColor || '#6d7cff'; ctx.fillRect(-w / 2, -h / 2, w, top); if (sub) { ctx.fillStyle = layer.backgroundColor || 'rgba(7,16,30,.92)'; ctx.fillRect(-w / 2, -h / 2 + top, w, h - top); }
      ctx.textAlign = 'right'; ctx.direction = 'rtl'; ctx.textBaseline = 'middle'; ctx.fillStyle = '#fff'; ctx.font = `800 ${clamp(layer.fontSize, 16, 120, 38)}px Arial`; ctx.fillText(title, w / 2 - 28, -h / 2 + top / 2, w - 56);
      if (sub) { ctx.fillStyle = layer.subtitleColor || '#e1eaf7'; ctx.font = `${clamp(layer.subtitleSize, 12, 80, 24)}px Arial`; ctx.fillText(sub, w / 2 - 28, -h / 2 + top + (h - top) / 2, w - 56); } },
    minimal(ctx, layer, w, h, title, sub) { ctx.shadowColor = 'rgba(0,0,0,.85)'; ctx.shadowBlur = 14; LT_TEXT(ctx, layer, w, h, title, sub, w / 2 - 6); ctx.shadowBlur = 0; ctx.fillStyle = layer.accentColor || '#6d7cff'; ctx.fillRect(w / 2 - Math.min(w * .5, 380), h / 2 - 6, Math.min(w * .5, 380), 5); }
  };
  function LT_TEXT(ctx, layer, w, h, title, sub, rx) {
    ctx.textAlign = 'right'; ctx.direction = 'rtl'; ctx.textBaseline = 'alphabetic'; ctx.fillStyle = layer.textColor || '#fff'; ctx.font = `700 ${clamp(layer.fontSize, 16, 120, 38)}px Arial`; ctx.fillText(title, rx, -h * .04, w - 50);
    if (sub) { ctx.fillStyle = layer.subtitleColor || '#d3dfef'; ctx.font = `${clamp(layer.subtitleSize, 12, 80, 24)}px Arial`; ctx.fillText(sub, rx, h * .30, w - 50); }
  }
  function drawLayer(ctx, layer, now, selected) {
    if (layer.visible === false && !exitActive(layer, now)) return;
    const x=clamp(layer.x,0,100,5)/100*W, y=clamp(layer.y,0,100,5)/100*H;
    const w=clamp(layer.w,2,100,20)/100*W, h=clamp(layer.h,2,100,12)/100*H;
    const anim=animation(layer,now), cx=x+w/2, cy=y+h/2;
    if (Math.abs(anim.sx) < .001 || Math.abs(anim.sy) < .001 || anim.alpha <= 0) return;
    const blurStr = anim.blur > .1 ? ` blur(${anim.blur.toFixed(1)}px)` : '';
    ctx.save(); ctx.globalAlpha=Math.max(0,Math.min(1,anim.alpha)); ctx.translate(cx+anim.dx,cy+anim.dy); ctx.rotate(anim.rot); ctx.scale(anim.sx,anim.sy);
    if (anim.glow) { ctx.shadowColor=anim.glow.color; ctx.shadowBlur=anim.glow.blur; }
    if (anim.clip) {
      const c=anim.clip, p=c.p; ctx.beginPath();
      if (c.dir==='l') ctx.rect(-w/2,-h/2,w*p,h); else if (c.dir==='r') ctx.rect(w/2-w*p,-h/2,w*p,h); else if (c.dir==='u') ctx.rect(-w/2,-h/2,w,h*p); else ctx.rect(-w/2,h/2-h*p,w,h*p);
      ctx.clip();
    }
    if (layer.type==='ticker' || layer.type==='countdown' || layer.type==='comment') { drawSpecial(ctx,layer,w,h,now,blurStr); ctx.restore(); return; }
    if (layer.type==='text' || layer.type==='lowerThird' || layer.type==='breaking') {
      const text = layer.text || (layer.type==='breaking'?'خبر فوری':''), title = layer.title || '';
      ctx.filter=colorFilter(layer.color || {})+blurStr;
      if (layer.type==='lowerThird' && LT_STYLES[layer.ltStyle]) { LT_STYLES[layer.ltStyle](ctx,layer,w,h,title||text,layer.subtitle); }
      else if (layer.type==='lowerThird') {
        ctx.fillStyle=layer.backgroundColor || 'rgba(7,16,30,.88)'; ctx.fillRect(-w/2,-h/2,w,h);
        ctx.fillStyle=layer.accentColor || '#6d7cff'; ctx.fillRect(w/2-9,-h/2,9,h);
        ctx.textAlign='right'; ctx.direction='rtl'; ctx.fillStyle=layer.textColor || '#fff'; ctx.font=`700 ${clamp(layer.fontSize,16,120,38)}px Arial`;
        ctx.fillText(title || text,w/2-26,-h*.04,w-50);
        if (layer.subtitle) { ctx.fillStyle=layer.subtitleColor || '#d3dfef'; ctx.font=`${clamp(layer.subtitleSize,12,80,24)}px Arial`; ctx.fillText(layer.subtitle,w/2-26,h*.30,w-50); }
      } else if (layer.type==='breaking') {
        ctx.fillStyle=layer.backgroundColor || '#b51f36'; ctx.fillRect(-w/2,-h/2,w,h);
        ctx.fillStyle=layer.tagColor || '#fff'; ctx.fillRect(w/2-260,-h/2+10,245,h-20);
        ctx.direction='rtl'; ctx.textAlign='center'; ctx.fillStyle=layer.tagTextColor || '#a51d31'; ctx.font='bold 25px Arial'; ctx.fillText(layer.tag || 'خبر فوری',w/2-138,9);
        ctx.textAlign='right'; ctx.fillStyle=layer.textColor || '#fff'; ctx.font=`bold ${clamp(layer.fontSize,16,100,30)}px Arial`; ctx.fillText(text,-w/2+w-290,10,w-320);
        if (layer.showClock) { ctx.textAlign='left'; ctx.font='24px Arial'; ctx.fillText(new Date().toLocaleTimeString('fa-IR',{hour:'2-digit',minute:'2-digit'}),-w/2+18,10); }
      } else {
        ctx.direction='rtl'; ctx.textAlign=layer.align || 'center'; ctx.textBaseline='middle'; ctx.fillStyle=layer.textColor || '#fff';
        ctx.font=`${layer.bold===false?'':'bold '}${clamp(layer.fontSize,12,160,52)}px ${layer.fontFamily || 'Arial'}`;
        ctx.shadowColor=layer.textShadow===false?'transparent':'rgba(0,0,0,.8)'; ctx.shadowBlur=layer.textShadow===false?0:12;
        const lines=String(text).split('\n'); const lineHeight=clamp(layer.lineHeight,1,2.5,1.2)*clamp(layer.fontSize,12,160,52);
        lines.forEach((line,i)=>ctx.fillText(line,0,(i-(lines.length-1)/2)*lineHeight,w));
      }
    } else {
      const asset = assetsCache.get(layer.assetId);
      if (asset) {
        ctx.filter=colorFilter(layer.color || {})+blurStr;
        if (layer.tint && layer.tint !== '#ffffff') { ctx.shadowColor=layer.tint; ctx.shadowBlur=layer.tintStrength || 0; }
        if (layer.type==='video' && asset.readyState<2) { /* wait for first decoded frame */ }
        else drawCover(ctx,asset,-w/2,-h/2,w,h,layer.fit || 'contain');
      }
    }
    ctx.restore();
  }
  function renderFrame(canvas, ctx, source, state, now, bypass) {
    const camFilter = bypass ? 'none' : colorFilter(state.camera);
    ctx.save(); ctx.clearRect(0,0,W,H); ctx.fillStyle='#101827'; ctx.fillRect(0,0,W,H);
    const bg=assetsCache.get(state.backgroundId);
    if (bg && (!(bg instanceof HTMLVideoElement) || bg.readyState>=2)) drawCover(ctx,bg,0,0,W,H,'cover');
    else { const bgc=state.bgGradient||['#1d304a','#070d16'],g=ctx.createLinearGradient(0,0,W,H); g.addColorStop(0,bgc[0]); g.addColorStop(1,bgc[1]); ctx.fillStyle=g; ctx.fillRect(0,0,W,H); }
    if (source && source.readyState>=2) {
      const f=state.cameraFrame, x=f.x/100*W,y=f.y/100*H,w=f.w/100*W,h=f.h/100*H;
      const pip=f.layout==='pip';
      if(pip){ctx.save();ctx.shadowColor='rgba(0,0,0,.65)';ctx.shadowBlur=26;ctx.fillStyle='rgba(255,255,255,.96)';ctx.fillRect(x-7,y-7,w+14,h+14);ctx.restore();}
      if(state.camera.chromaEnabled){
        const scale=Math.min(1,960/Math.max(1,w),540/Math.max(1,h)),kw=Math.max(1,Math.round(w*scale)),kh=Math.max(1,Math.round(h*scale));
        if(!keyCanvas){keyCanvas=document.createElement('canvas');keyContext=keyCanvas.getContext('2d',{willReadFrequently:true});}
        if(keyCanvas.width!==kw||keyCanvas.height!==kh){keyCanvas.width=kw;keyCanvas.height=kh;}
        keyContext.clearRect(0,0,kw,kh);keyContext.save();keyContext.filter=camFilter;drawCover(keyContext,source,0,0,kw,kh,f.fit||'cover');keyContext.restore();
        try{const frame=keyContext.getImageData(0,0,kw,kh),d=frame.data,hex=state.camera.chromaColor||'#00ff00',key=[1,3,5].map(i=>parseInt(hex.slice(i,i+2),16)),threshold=clamp(state.camera.chromaTolerance,5,240,90),soft=Math.max(1,clamp(state.camera.chromaSoftness,0,150,35));for(let p=0;p<d.length;p+=4){const dr=d[p]-key[0],dg=d[p+1]-key[1],db=d[p+2]-key[2],dist=Math.sqrt(dr*dr+dg*dg+db*db);d[p+3]=Math.round(d[p+3]*Math.max(0,Math.min(1,(dist-threshold)/soft)));}keyContext.putImageData(frame,0,0);}catch(e){console.warn('Chroma key processing skipped',e);}
        ctx.drawImage(keyCanvas,x,y,w,h);
      } else {ctx.save();ctx.filter=camFilter;drawCover(ctx,source,x,y,w,h,f.fit||'cover');ctx.restore();}
    }
    for(const layer of state.layers.slice().sort((a,b)=>(a.z||0)-(b.z||0)))drawLayer(ctx,layer,now,null);
    ctx.restore();
  }
  function render(instance, now) {
    renderFrame(instance.canvas,instance.ctx,instance.source,readState(instance.mode),now,instance.bypass);
    if(instance.programCanvas&&instance.programCtx)renderFrame(instance.programCanvas,instance.programCtx,instance.source,readProgramState(instance.mode),now);
    instance.frame=requestAnimationFrame(t=>render(instance,t));
  }
  function overlaySelection(instance) {
    const el=instance.selection, canvas=instance.canvas, layer=readState(instance.mode).layers.find(x=>x.id===instance.selectedId);
    if (!el || !layer) { if(el) el.style.display='none'; return; }
    const rect=canvas.getBoundingClientRect();
    el.classList.toggle('ls-empty-frame',(layer.type==='image'||layer.type==='video')&&!layer.assetId); el.style.display='block'; el.style.left=`${layer.x}%`; el.style.top=`${layer.y}%`; el.style.width=`${layer.w}%`; el.style.height=`${layer.h}%`;
  }
  function attachPointer(instance) {
    const canvas=instance.canvas, sel=instance.selection;
    let drag=null;
    const pointerDown=e=>{
      const rect=canvas.getBoundingClientRect(), x=(e.clientX-rect.left)/rect.width*100,y=(e.clientY-rect.top)/rect.height*100;
      const layers=readState(instance.mode).layers;
      const hit=layers.slice().sort((a,b)=>(b.z||0)-(a.z||0)).find(l=>l.visible!==false && x>=l.x && x<=l.x+l.w && y>=l.y && y<=l.y+l.h);
      if (!hit) { instance.selectedId=null; overlaySelection(instance); instance.onState?.(); return; }
      instance.selectedId=hit.id; if(hit.locked) { overlaySelection(instance); instance.onState?.(); return; }
      drag={id:hit.id,x:e.clientX,y:e.clientY,lx:hit.x,ly:hit.y}; canvas.setPointerCapture(e.pointerId); overlaySelection(instance); instance.onState?.();
    };
    const pointerMove=e=>{ if(!drag)return; const rect=canvas.getBoundingClientRect(), l=readState(instance.mode).layers.find(x=>x.id===drag.id); if(!l)return; l.x=clamp(drag.lx+(e.clientX-drag.x)/rect.width*100,0,100-l.w,0); l.y=clamp(drag.ly+(e.clientY-drag.y)/rect.height*100,0,100-l.h,0); overlaySelection(instance); save(instance.mode); };
    const pointerUp=()=>{if(drag){drag=null;instance.onState?.();}};
    canvas.addEventListener('pointerdown',pointerDown); canvas.addEventListener('pointermove',pointerMove); canvas.addEventListener('pointerup',pointerUp); canvas.addEventListener('pointercancel',pointerUp);
    instance.cleanup.push(()=>{canvas.removeEventListener('pointerdown',pointerDown);canvas.removeEventListener('pointermove',pointerMove);canvas.removeEventListener('pointerup',pointerUp);canvas.removeEventListener('pointercancel',pointerUp);});
    if(sel){
      sel.addEventListener('pointerdown',e=>{if(e.target===sel)e.preventDefault();});
      // Resize handles (corners keep the aspect ratio of media; hold Shift for free resize).
      let rz=null;
      const DIRS=['nw','n','ne','e','se','s','sw','w'];
      sel.querySelectorAll('.ls-handle').forEach(h=>h.remove());
      for(const d of DIRS){
        const h=document.createElement('span'); h.className='ls-handle ls-h-'+d; h.dataset.dir=d; sel.appendChild(h);
        h.addEventListener('pointerdown',e=>{
          const l=readState(instance.mode).layers.find(x=>x.id===instance.selectedId); if(!l||l.locked)return;
          e.preventDefault(); e.stopPropagation(); h.setPointerCapture(e.pointerId);
          rz={dir:d,x:e.clientX,y:e.clientY,l:{x:l.x,y:l.y,w:l.w,h:l.h},id:l.id,media:(l.type==='image'||l.type==='video')};
        });
        h.addEventListener('pointermove',e=>{
          if(!rz||rz.dir!==d)return;
          const l=readState(instance.mode).layers.find(x=>x.id===rz.id); if(!l)return;
          const rect=canvas.getBoundingClientRect(), dx=(e.clientX-rz.x)/rect.width*100, dy=(e.clientY-rz.y)/rect.height*100, o=rz.l, MIN=2;
          let nx=o.x,ny=o.y,nw=o.w,nh=o.h;
          if(d.includes('e'))nw=o.w+dx; if(d.includes('w')){nw=o.w-dx;nx=o.x+dx;}
          if(d.includes('s'))nh=o.h+dy; if(d.includes('n')){nh=o.h-dy;ny=o.y+dy;}
          const corner=d.length===2, lock=corner&&(rz.media!==e.shiftKey), ratio=(o.w*W)/(o.h*H);
          if(lock){
            nw=Math.max(MIN,nw); nh=nw*W/(H*ratio);
            // keep the opposite corner fixed
            if(d.includes('w'))nx=o.x+o.w-nw; if(d.includes('n'))ny=o.y+o.h-nh;
            for(let k=0;k<2;k++){
              if(nx<0){nw+=nx;nx=0;} if(ny<0){nh+=ny;ny=0;}
              if(lock){ if(nw*W/(H*ratio)<nh)nh=nw*W/(H*ratio); else nw=nh*H*ratio/W; }
              if(nx+nw>100){nw=100-nx;} if(ny+nh>100){nh=100-ny;}
              if(lock){ const hh=nw*W/(H*ratio); if(hh<nh)nh=hh; else nw=nh*H*ratio/W; }
            }
            if(d.includes('w'))nx=o.x+o.w-nw; if(d.includes('n'))ny=o.y+o.h-nh;
          } else {
            nw=Math.max(MIN,nw); nh=Math.max(MIN,nh);
            if(d.includes('w'))nx=o.x+o.w-nw; if(d.includes('n'))ny=o.y+o.h-nh;
            if(nx<0){nw+=nx;nx=0;} if(ny<0){nh+=ny;ny=0;}
            nw=Math.min(nw,100-nx); nh=Math.min(nh,100-ny);
          }
          l.x=+nx.toFixed(2); l.y=+ny.toFixed(2); l.w=+Math.max(MIN,nw).toFixed(2); l.h=+Math.max(MIN,nh).toFixed(2);
          overlaySelection(instance); save(instance.mode);
        });
        const end=e=>{if(rz&&rz.dir===d){rz=null;try{h.releasePointerCapture(e.pointerId);}catch(_){}instance.onState?.();}};
        h.addEventListener('pointerup',end); h.addEventListener('pointercancel',end);
      }
    }
  }
  function fitLayerToAsset(l, el) {
    const sz = sourceSize(el); if (!sz.w || !sz.h) return;
    let h = l.w * (W / H) * (sz.h / sz.w), w = l.w;
    if (h > 90) { w = w * 90 / h; h = 90; }
    l.w = clamp(w, 2, 100, l.w); l.h = clamp(h, 2, 100, l.h);
    l.x = clamp(l.x, 0, 100 - l.w, 0); l.y = clamp(l.y, 0, 100 - l.h, 0);
  }
  const sceneSig = st => JSON.stringify({b:st.backgroundId||null,f:st.cameraFrame,l:(st.layers||[]).map(l=>{const c={...l};delete c.startedAt;delete c.exitAt;return c;})});
  function getInstance(mode){return instances.get(mode);}
  const PRESETS = {
    natural:{brightness:100,contrast:100,saturation:100,exposure:0,hue:0},
    warm:{brightness:104,contrast:104,saturation:112,exposure:4,hue:-8},
    cool:{brightness:100,contrast:104,saturation:94,exposure:0,hue:10},
    cinematic:{brightness:94,contrast:122,saturation:88,exposure:-6,hue:-4},
    vivid:{brightness:102,contrast:112,saturation:140,exposure:0,hue:0},
    bright:{brightness:112,contrast:96,saturation:104,exposure:14,hue:0},
    mono:{brightness:100,contrast:112,saturation:0,exposure:0,hue:0}
  };
  const syncProgramCamera = mode => { if (mode !== 'live') return; const p = readProgramState(mode), s = readState(mode); p.camera = { ...s.camera }; saveProgram(mode); };
  window.studioCompositor = {
    async attach(canvasId, sourceVideoId, mode, programCanvasId = null) {
      await refreshAssets();
      const canvas=document.getElementById(canvasId), source=document.getElementById(sourceVideoId);
      if(!canvas||!source) throw new Error('بوم ترکیب تصویر یا منبع دوربین پیدا نشد.');
      const old=instances.get(mode); if(old){if(old.frame)cancelAnimationFrame(old.frame);old.cleanup.forEach(fn=>fn());}
      canvas.width=W; canvas.height=H;
      const ctx=canvas.getContext('2d',{alpha:false,willReadFrequently:true});
      const programCanvas=programCanvasId?document.getElementById(programCanvasId):null; if(programCanvas){programCanvas.width=W;programCanvas.height=H;}
      const programCtx=programCanvas?.getContext('2d',{alpha:false,willReadFrequently:true})||null;
      if(mode==='live')readProgramState(mode);
      const selection=canvas.parentElement?.querySelector('.ls-selection-box') || null;
      const instance={canvas,ctx,source,mode,selection,programCanvas,programCtx,frame:0,cleanup:[],selectedId:null,onState:null};
      instances.set(mode,instance); attachPointer(instance); render(instance,performance.now()); return this.getState(mode);
    },
    async refreshAssets(){return await refreshAssets();},
    getAssets(){return assetList.map(a=>({id:a.id,name:a.name,mimeType:a.mimeType,category:a.category,url:a.url}));},
    getState(mode){const s=readState(mode);s.layers.forEach(normAnim);return {backgroundId:s.backgroundId,camera:s.camera,cameraFrame:s.cameraFrame,layers:s.layers,selectedId:getInstance(mode)?.selectedId||null,programLayers:mode==='live'?readProgramState(mode).layers.length:0,programDirty:mode==='live'?sceneSig(s)!==sceneSig(readProgramState(mode)):false,compare:!!getInstance(mode)?.bypass};},
    replayAnimation(mode,layerId){const l=readState(mode).layers.find(x=>x.id===layerId);if(!l)return this.getState(mode);l.visible=true;delete l.exitAt;l.startedAt=performance.now();save(mode);return this.getState(mode);},
    previewExit(mode,layerId){const s=readState(mode),l=s.layers.find(x=>x.id===layerId);if(!l)return this.getState(mode);normAnim(l);if(l.animOut==='none')throw new Error('ابتدا یک انیمیشن خروج انتخاب کنید.');l.visible=true;l.exitAt=performance.now();l.visible=false;save(mode);const back=Math.round(num(l.animOutDur,.1,3,.5)*1000)+450;setTimeout(()=>{const cur=readState(mode).layers.find(x=>x.id===layerId);if(cur&&cur.visible===false){cur.visible=true;delete cur.exitAt;cur.startedAt=performance.now();save(mode);getInstance(mode)?.onState?.();}},back);getInstance(mode)?.onState?.();return this.getState(mode);},
    applyAnimPreset(mode,layerId,key){const l=readState(mode).layers.find(x=>x.id===layerId);if(!l)return this.getState(mode);const p=ANIM_PRESETS[key];if(!p)throw new Error('پریست انیمیشن پیدا نشد.');normAnim(l);Object.assign(l,ANIM_DEFAULTS,p);l.visible=true;delete l.exitAt;l.startedAt=performance.now();save(mode);return this.getState(mode);},
    selectLayer(mode,layerId){const i=getInstance(mode);if(i){i.selectedId=layerId||null;overlaySelection(i);i.onState?.();}return this.getState(mode);},
    addLayer(mode,assetId,type){const s=readState(mode),a=assetById(assetId);if(!NOASSET.includes(type)&&!a)throw new Error('ابتدا یک رسانه معتبر انتخاب کنید.');if(a&&a.mimeType?.startsWith('video/'))type='video';else if(a&&a.mimeType?.startsWith('image/'))type='image';const l={id:id(),type:type||'image',assetId:assetId||null,text:type==='breaking'?'تیتر خبر فوری':type==='text'?'متن جدید روی تصویر':'',title:type==='lowerThird'?'نام مجری':'',subtitle:'',x: type==='breaking'?0:8,y:type==='breaking'?88:8,w:type==='breaking'?100: type==='lowerThird'?55:24,h:type==='breaking'?12:type==='lowerThird'?10:18,opacity:100,visible:true,locked:false,z:s.layers.length+1,animIn:'none',animLoop:'none',animOut:'none',startedAt:performance.now(),fontSize:type==='breaking'?30:48,textColor:'#ffffff',backgroundColor:type==='breaking'?'#b51f36':'rgba(7,16,30,.88)',accentColor:'#6d7cff',color:{brightness:100,contrast:100,saturation:100,exposure:0,hue:0}};if(TYPE_DEFAULTS[type])Object.assign(l,JSON.parse(JSON.stringify(TYPE_DEFAULTS[type])));if(a)fitLayerToAsset(l,assetsCache.get(assetId));s.layers.push(l);save(mode);const i=getInstance(mode);if(i){i.selectedId=l.id;overlaySelection(i);i.onState?.();}return this.getState(mode);},
    setBackground(mode,assetId){const s=readState(mode);if(assetId){const a=assetById(assetId);if(!a)throw new Error('رسانه انتخاب‌شده پیدا نشد.');s.backgroundId=assetId;}else{s.backgroundId=null;}save(mode);const bi=getInstance(mode);bi?.onState?.();return this.getState(mode);},
    updateCamera(mode,key,value){const s=readState(mode);if(key==='chromaEnabled')s.camera[key]=!!value;else if(key==='chromaColor')s.camera[key]=value;else s.camera[key]=Number(value);save(mode);syncProgramCamera(mode);return this.getState(mode);},
    applyColorPreset(mode,name){const p=PRESETS[name];if(!p)return this.getState(mode);const s=readState(mode);Object.assign(s.camera,p);save(mode);syncProgramCamera(mode);const i=getInstance(mode);i?.onState?.();return this.getState(mode);},
    resetColor(mode){return this.applyColorPreset(mode,'natural');},
    setCompare(mode,on){const i=getInstance(mode);if(!i)return false;if(on&&mode==='creator'){try{const st=window.creatorStudio?.recordingStatus?.();if(st&&st.state!=='inactive')return false;}catch(_){}}i.bypass=!!on;return i.bypass;},
    setLayerAsset(mode,layerId,assetId){const s=readState(mode),l=s.layers.find(x=>x.id===layerId);if(!l)return this.getState(mode);if(!assetId){l.assetId=null;}else{const a=assetById(assetId);if(!a)throw new Error('رسانه انتخاب‌شده پیدا نشد.');l.assetId=assetId;l.type=a.mimeType?.startsWith('video/')?'video':'image';fitLayerToAsset(l,assetsCache.get(assetId));}l.startedAt=performance.now();save(mode);const i=getInstance(mode);if(i){overlaySelection(i);i.onState?.();}return this.getState(mode);},
    addEmptyFrame(mode,kind){const s=readState(mode);const l={id:id(),type:kind==='video'?'video':'image',assetId:null,text:'',title:'',subtitle:'',x:8,y:8,w:24,h:24*(W/H)*0.5625*1.0,opacity:100,visible:true,locked:false,z:s.layers.length+1,animIn:'none',animLoop:'none',animOut:'none',startedAt:performance.now(),fontSize:48,textColor:'#ffffff',backgroundColor:'rgba(7,16,30,.88)',accentColor:'#6d7cff',fit:'contain',color:{brightness:100,contrast:100,saturation:100,exposure:0,hue:0}};l.h=clamp(l.w*0.5625,2,100);s.layers.push(l);save(mode);const i=getInstance(mode);if(i){i.selectedId=l.id;overlaySelection(i);i.onState?.();}return this.getState(mode);},
    updateCameraFrame(mode,key,value){const s=readState(mode);if(key==='layout'){if(value==='pip')s.cameraFrame={x:68,y:62,w:28,h:30,fit:'cover',layout:'pip'};else if(value==='left')s.cameraFrame={x:0,y:0,w:50,h:100,fit:'cover',layout:'left'};else if(value==='right')s.cameraFrame={x:50,y:0,w:50,h:100,fit:'cover',layout:'right'};else s.cameraFrame={x:0,y:0,w:100,h:100,fit:'cover',layout:'full'};}else s.cameraFrame[key]=Number(value);save(mode);return this.getState(mode);},
    updateLayer(mode,layerId,key,value){const l=readState(mode).layers.find(x=>x.id===layerId);if(!l)return this.getState(mode);normAnim(l);if(key==='visible'){const v=!!value,was=l.visible!==false;l.visible=v;if(v&&!was){l.startedAt=performance.now();delete l.exitAt;}else if(!v&&was){l.exitAt=performance.now();}}else if(key==='locked'||key==='showClock'||key==='bold'||key==='textShadow'||key==='cdAutoHide')l[key]=!!value;else if(key==='cdTotal'){l.cdTotal=Math.max(1,Number(value)||1);if(!l.endAt)l.pausedLeft=l.cdTotal*1000;}else if(['text','title','subtitle','textColor','backgroundColor','accentColor','tag','fit','glowColor','animIn','animLoop','animOut','animInEase','animCycle','ltStyle','tickerDir','doneText','author','amount'].includes(key)){l[key]=value;if(key==='animIn'||key==='animInEase'||key==='animCycle')l.startedAt=performance.now();}else if(['animInDur','animInDelay','animDistance','animHold','animGap'].includes(key)){l[key]=Number(value);l.startedAt=performance.now();}else if(key.startsWith('color.')){l.color=l.color||{};l.color[key.slice(6)]=Number(value);}else l[key]=Number(value);save(mode);const i=getInstance(mode);if(i)overlaySelection(i);return this.getState(mode);},
    moveLayer(mode,layerId,direction){const s=readState(mode),ordered=s.layers.slice().sort((a,b)=>(a.z||0)-(b.z||0)),index=ordered.findIndex(x=>x.id===layerId),target=index+Number(direction);if(index<0||target<0||target>=ordered.length)return this.getState(mode);const a=ordered[index],b=ordered[target],[za,zb]=[a.z,b.z];a.z=zb;b.z=za;save(mode);return this.getState(mode);},
    deleteLayer(mode,layerId){const s=readState(mode);s.layers=s.layers.filter(x=>x.id!==layerId);const i=getInstance(mode);if(i){i.selectedId=null;overlaySelection(i);i.onState?.();}save(mode);return this.getState(mode);},
    async outputStream(mode){const i=getInstance(mode);if(!i)throw new Error('موتور ترکیب تصویر هنوز آماده نشده است.');const target=mode==='live'?(i.programCanvas||i.canvas):i.canvas;return target.captureStream(30);},
    addBanner(mode,key,overrides){const spec=BANNERS[key];if(!spec)throw new Error('بنر پیدا نشد.');const s=readState(mode),now=performance.now();const base={id:id(),type:spec.type,assetId:null,text:'',title:'',subtitle:'',x:8,y:8,w:24,h:18,opacity:100,visible:true,locked:false,z:s.layers.length+10,startedAt:now,fontSize:40,textColor:'#ffffff',backgroundColor:'rgba(7,16,30,.88)',accentColor:'#6d7cff',color:{brightness:100,contrast:100,saturation:100,exposure:0,hue:0},animIn:'slide-up',animInDur:.55,animDistance:18,animOut:'slide-down',animOutDur:.45,animLoop:'none'};const l=Object.assign(base,JSON.parse(JSON.stringify(TYPE_DEFAULTS[spec.type]||{})),JSON.parse(JSON.stringify(spec)),overrides||{});l.id=base.id;const copy=JSON.parse(JSON.stringify(l));s.layers.push(l);save(mode);if(mode==='live'){const p=readProgramState(mode);p.layers.push(copy);saveProgram(mode);}const i=getInstance(mode);if(i){i.selectedId=l.id;overlaySelection(i);i.onState?.();}return this.getState(mode);},
    setLayerLive(mode,layerId,visible){const now=performance.now();both(mode,st=>{const l=st.layers.find(x=>x.id===layerId);if(!l)return;normAnim(l);const was=l.visible!==false;l.visible=!!visible;if(visible&&!was){l.startedAt=now;delete l.exitAt;}else if(!visible&&was)l.exitAt=now;});const i=getInstance(mode);i?.onState?.();return this.getState(mode);},
    showComment(mode,author,text,amount){const now=performance.now();let target=null;both(mode,st=>{let l=st.layers.find(x=>x.type==='comment');if(!l){l=Object.assign({id:target||id(),type:'comment',assetId:null,title:'',subtitle:'',opacity:100,visible:false,locked:false,z:st.layers.length+20,color:{brightness:100,contrast:100,saturation:100,exposure:0,hue:0},animLoop:'none',animInDelay:0,animDistance:18},JSON.parse(JSON.stringify(TYPE_DEFAULTS.comment)));st.layers.push(l);}target=l.id;normAnim(l);l.author=String(author||'').slice(0,60);l.text=String(text||'').slice(0,300);l.amount=String(amount||'').slice(0,20);l.visible=true;l.startedAt=now;delete l.exitAt;});getInstance(mode)?.onState?.();return this.getState(mode);},
    hideComment(mode){const now=performance.now();both(mode,st=>{const l=st.layers.find(x=>x.type==='comment');if(l&&l.visible!==false){normAnim(l);l.visible=false;l.exitAt=now;}});getInstance(mode)?.onState?.();return this.getState(mode);},
    startCountdown(mode,layerId,seconds){const now=Date.now();both(mode,st=>{const l=st.layers.find(x=>x.id===layerId);if(!l)return;const sec=Number(seconds)>0?Number(seconds):(l.cdTotal||300);l.cdTotal=sec;const left=l.endAt?Math.max(0,l.endAt-now):(l.pausedLeft!=null&&l.pausedLeft>0?l.pausedLeft:sec*1000);l.endAt=now+left;delete l.pausedLeft;});getInstance(mode)?.onState?.();return this.getState(mode);},
    pauseCountdown(mode,layerId){const now=Date.now();both(mode,st=>{const l=st.layers.find(x=>x.id===layerId);if(!l||!l.endAt)return;l.pausedLeft=Math.max(0,l.endAt-now);delete l.endAt;});getInstance(mode)?.onState?.();return this.getState(mode);},
    resetCountdown(mode,layerId){both(mode,st=>{const l=st.layers.find(x=>x.id===layerId);if(!l)return;delete l.endAt;l.pausedLeft=(l.cdTotal||300)*1000;});getInstance(mode)?.onState?.();return this.getState(mode);},
    getAnimConfig(mode,layerId){const l=readState(mode).layers.find(x=>x.id===layerId);if(!l)return null;normAnim(l);const o={};ANIM_KEYS.forEach(k=>{if(l[k]!==undefined)o[k]=l[k];});return JSON.stringify(o);},
    applyAnimConfig(mode,layerId,json){const l=readState(mode).layers.find(x=>x.id===layerId);if(!l)return this.getState(mode);let cfg;try{cfg=JSON.parse(json);}catch{throw new Error('پریست معتبر نیست.');}normAnim(l);Object.assign(l,ANIM_DEFAULTS);ANIM_KEYS.forEach(k=>{if(cfg[k]!==undefined)l[k]=cfg[k];});l.visible=true;delete l.exitAt;l.startedAt=performance.now();save(mode);return this.getState(mode);},
    listTemplates(){return TEMPLATES.map(t=>({key:t.key,label:t.label,icon:t.icon}));},
    applyTemplate(mode,key,live){const t=TEMPLATES.find(x=>x.key===key);if(!t)throw new Error('قالب پیدا نشد.');const fn=st=>{st.layers=t.layers.map((sp,i)=>newLayer(sp,i+1));st.cameraFrame=Object.assign({},t.camera);st.bgGradient=t.bg.slice();st.backgroundId=null;};fn(readState(mode));save(mode);if(live&&mode==='live'){fn(readProgramState(mode));saveProgram(mode);}const i=getInstance(mode);if(i){i.selectedId=null;overlaySelection(i);i.onState?.();}return this.getState(mode);},
    bannerOn(mode,key,over){const o=pick(over,['title','subtitle','text']);let found=false;const now=performance.now();both(mode,st=>{const l=st.layers.find(x=>x.bannerKey===key);if(l){found=true;Object.assign(l,o);normAnim(l);l.visible=true;l.startedAt=now;delete l.exitAt;}});if(!found)this.addBanner(mode,key,Object.assign({bannerKey:key},o));getInstance(mode)?.onState?.();return true;},
    bannerOff(mode,key){const now=performance.now();both(mode,st=>{const l=st.layers.find(x=>x.bannerKey===key);if(l&&l.visible!==false){normAnim(l);l.visible=false;l.exitAt=now;}});getInstance(mode)?.onState?.();return true;},
    bannerKeys(){return Object.keys(BANNERS);},
    activeBanners(mode){return readProgramState(mode).layers.filter(l=>l.bannerKey&&l.visible!==false).map(l=>l.bannerKey);},
    firstLayerId(mode,type){return readState(mode).layers.find(l=>l.type===type)?.id||null;},
    playMedia(mode,assetId,title){const a=assetById(assetId);if(!a||!a.mimeType?.startsWith('video/'))throw new Error('ویدئو پیدا نشد.');const v=assetsCache.get(assetId);if(!(v instanceof HTMLVideoElement))throw new Error('ویدئو هنوز بارگذاری نشده است؛ چند ثانیه بعد دوباره تلاش کنید.');
      if(mediaPlaying)this.stopMedia(mode);
      const now=performance.now();both(mode,st=>{removeMediaLayers(st);const l=newLayer({type:'video',assetId,x:0,y:0,w:100,h:100,fit:'contain',mediaPlay:true,animIn:'fade',animInDur:.4,animOut:'fade',animOutDur:.4},900);l.startedAt=now;st.layers.push(l);});
      try{v.loop=false;v.muted=false;v.currentTime=0;window.lsSfx?.routeMedia(v);const done=()=>{v.removeEventListener('ended',done);if(mediaPlaying&&mediaPlaying.v===v)window.studioCompositor.stopMedia(mode,true);};v.addEventListener('ended',done);mediaPlaying={assetId,title:title||a.name||'ویدئو',v,done};v.play().catch(()=>{});}catch(e){mediaPlaying=null;both(mode,removeMediaLayers);throw e;}
      getInstance(mode)?.onState?.();window.lsTeam?.mediaChanged?.(mediaPlaying?{assetId,title:mediaPlaying.title}:null);return true;},
    stopMedia(mode,ended){const m=mediaPlaying;mediaPlaying=null;if(m){try{m.v.removeEventListener('ended',m.done);m.v.pause();m.v.muted=true;m.v.loop=true;m.v.currentTime=0;m.v.play().catch(()=>{});}catch(_){}}
      const now=performance.now();both(mode,st=>{st.layers.forEach(l=>{if(l.mediaPlay&&l.visible!==false){normAnim(l);l.visible=false;l.exitAt=now;}});});
      setTimeout(()=>{both(mode,removeMediaLayers);getInstance(mode)?.onState?.();},700);
      window.lsTeam?.mediaChanged?.(null,!!ended);return true;},
    currentMedia(){return mediaPlaying?{assetId:mediaPlaying.assetId,title:mediaPlaying.title}:null;},
    takeProgram(mode){const i=getInstance(mode);if(!i?.programCanvas)throw new Error('مانیتور Program در این استودیو موجود نیست.');const prev=new Map(readProgramState(mode).layers.map(l=>[l.id,l])),now=performance.now(),state=JSON.parse(JSON.stringify(readState(mode)));state.layers.forEach(layer=>{normAnim(layer);const old=prev.get(layer.id),wasOn=old&&old.visible!==false;if(layer.visible!==false){layer.startedAt=wasOn&&old.startedAt?old.startedAt:now;delete layer.exitAt;}else{layer.startedAt=now;if(wasOn)layer.exitAt=now;else delete layer.exitAt;}});programStateByMode.set(mode,state);saveProgram(mode);i.onState?.();return {layers:state.layers.length,backgroundId:state.backgroundId};},
    stop(mode){const i=getInstance(mode);if(!i)return;cancelAnimationFrame(i.frame);i.cleanup.forEach(fn=>fn());instances.delete(mode);},
    setStateCallback(mode,callback){const i=getInstance(mode);if(i)i.onState=()=>callback?.invokeMethodAsync?.('OnComposerChanged',this.getState(mode));},
    listScenes(mode){try{return JSON.parse(localStorage.getItem(`${STORAGE}.${mode}.scenes`)||'[]').map(({id,name,updatedAt})=>({id,name,updatedAt}));}catch{return []; }},
    saveScene(mode,name){const title=String(name||'').trim()||'صحنه بدون نام';let scenes=[];try{scenes=JSON.parse(localStorage.getItem(`${STORAGE}.${mode}.scenes`)||'[]');}catch{}const scene={id:id(),name:title,updatedAt:new Date().toISOString(),state:JSON.parse(JSON.stringify(readState(mode)))};scenes.unshift(scene);localStorage.setItem(`${STORAGE}.${mode}.scenes`,JSON.stringify(scenes.slice(0,40)));return this.listScenes(mode);},
    loadScene(mode,sceneId){let scenes=[];try{scenes=JSON.parse(localStorage.getItem(`${STORAGE}.${mode}.scenes`)||'[]');}catch{}const scene=scenes.find(x=>x.id===sceneId);if(!scene?.state)throw new Error('صحنه ذخیره‌شده پیدا نشد.');stateByMode.set(mode,scene.state);stateByMode.get(mode).layers.forEach(layer=>{normAnim(layer);delete layer.exitAt;layer.startedAt=performance.now();});save(mode);const i=getInstance(mode);if(i){i.selectedId=null;overlaySelection(i);i.onState?.();}return this.getState(mode);},
    exportScene(mode){const payload={format:'LiveStudioScene',version:1,mode,exportedAt:new Date().toISOString(),state:readState(mode)};const blob=new Blob([JSON.stringify(payload,null,2)],{type:'application/json'});const url=URL.createObjectURL(blob);const a=document.createElement('a');a.href=url;a.download=`livestudio-${mode}-scene.json`;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);},
    importScene(mode){return new Promise((resolve,reject)=>{const input=document.createElement('input');input.type='file';input.accept='.json,application/json';input.oncancel=()=>resolve(null);input.onchange=async()=>{try{const file=input.files?.[0];if(!file){resolve(null);return;}const payload=JSON.parse(await file.text());const state=payload?.format==='LiveStudioScene'?payload.state:payload;if(!state||!Array.isArray(state.layers)||!state.camera||!state.cameraFrame)throw new Error('ساختار فایل صحنه معتبر نیست.');stateByMode.set(mode,state);state.layers.forEach(layer=>{normAnim(layer);delete layer.exitAt;layer.startedAt=performance.now();});save(mode);const i=getInstance(mode);if(i){i.selectedId=null;overlaySelection(i);i.onState?.();}resolve(this.getState(mode));}catch(error){reject(error);}};input.click();});},
    snapshot(canvasId,mode){const canvas=document.getElementById(canvasId);if(!canvas)throw new Error('مانیتور Preview پیدا نشد.');canvas.toBlob(blob=>{if(!blob)return;const url=URL.createObjectURL(blob);const a=document.createElement('a');a.href=url;a.download=`livestudio-${mode}-preview.png`;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);},'image/png');},
    reset(mode){stateByMode.set(mode,defaults());save(mode);const i=getInstance(mode);if(i){i.selectedId=null;overlaySelection(i);i.onState?.();}return this.getState(mode);}
  };
})();
