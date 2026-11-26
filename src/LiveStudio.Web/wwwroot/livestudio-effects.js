(() => {
  const clamp = (v, min, max, fallback) => Number.isFinite(Number(v)) ? Math.min(max, Math.max(min, Number(v))) : fallback;
  const defaults = Object.freeze({ brightness: 100, contrast: 100, saturation: 100, exposure: 0, chromaEnabled: false, chromaColor: '#00ff00', chromaTolerance: 90, chromaSoftness: 35 });
  let settings = { ...defaults };
  let workCanvas = null, workContext = null;
  const normalize = value => ({
    brightness: clamp(value?.brightness, 25, 200, defaults.brightness),
    contrast: clamp(value?.contrast, 25, 200, defaults.contrast),
    saturation: clamp(value?.saturation, 0, 250, defaults.saturation),
    exposure: clamp(value?.exposure, -100, 100, defaults.exposure),
    chromaEnabled: !!value?.chromaEnabled,
    chromaColor: /^#[0-9a-f]{6}$/i.test(value?.chromaColor || '') ? value.chromaColor : defaults.chromaColor,
    chromaTolerance: clamp(value?.chromaTolerance, 10, 220, defaults.chromaTolerance),
    chromaSoftness: clamp(value?.chromaSoftness, 0, 120, defaults.chromaSoftness)
  });
  const get = () => settings;
  const set = value => { settings = normalize(value); return settings; };
  const cover = (ctx, source, width, height) => {
    const sw = source.videoWidth || source.naturalWidth || source.width;
    const sh = source.videoHeight || source.naturalHeight || source.height;
    if (!sw || !sh) return;
    const scale = Math.max(width / sw, height / sh), w = sw * scale, h = sh * scale;
    ctx.drawImage(source, (width - w) / 2, (height - h) / 2, w, h);
  };
  const parseColor = hex => [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16));
  function drawCover(ctx, source, width, height) {
    const s = settings;
    if (!s.chromaEnabled) {
      ctx.save();
      const brightness = s.brightness * Math.pow(2, s.exposure / 100);
      ctx.filter = `brightness(${brightness}%) contrast(${s.contrast}%) saturate(${s.saturation}%)`;
      cover(ctx, source, width, height);
      ctx.restore();
      return;
    }
    const scale = Math.min(1, 1280 / width, 720 / height);
    const rw = Math.max(1, Math.round(width * scale)), rh = Math.max(1, Math.round(height * scale));
    if (!workCanvas) { workCanvas = document.createElement('canvas'); workContext = workCanvas.getContext('2d', { willReadFrequently: true }); }
    if (workCanvas.width !== rw || workCanvas.height !== rh) { workCanvas.width = rw; workCanvas.height = rh; }
    const brightness = s.brightness * Math.pow(2, s.exposure / 100);
    workContext.save();
    workContext.clearRect(0, 0, rw, rh);
    workContext.filter = `brightness(${brightness}%) contrast(${s.contrast}%) saturate(${s.saturation}%)`;
    cover(workContext, source, rw, rh);
    workContext.restore();
    try {
      const frame = workContext.getImageData(0, 0, rw, rh), data = frame.data, key = parseColor(s.chromaColor);
      const threshold = s.chromaTolerance, softness = Math.max(1, s.chromaSoftness);
      for (let i = 0; i < data.length; i += 4) {
        const dr = data[i] - key[0], dg = data[i + 1] - key[1], db = data[i + 2] - key[2];
        const distance = Math.sqrt(dr * dr + dg * dg + db * db);
        const alpha = Math.max(0, Math.min(1, (distance - threshold) / softness));
        data[i + 3] = Math.round(data[i + 3] * alpha);
      }
      workContext.putImageData(frame, 0, 0);
      ctx.drawImage(workCanvas, 0, 0, width, height);
    } catch (error) {
      console.warn('LiveStudio chroma key skipped:', error);
      ctx.drawImage(workCanvas, 0, 0, width, height);
    }
  }
  window.lsSceneEffects = { defaults, get, set, drawCover };
})();
