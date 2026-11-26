(() => {
  const defaults = { enabled: true, inputGain: 100, highPass: 80, lowGain: 0, midGain: 0, highGain: 0, compressor: true, threshold: -24, ratio: 3, noiseSuppression: true, echoCancellation: true, autoGainControl: false };
  let settings = { ...defaults }, testStream = null, testGraph = null, meterFrame = 0;
  const activeGraphs = new Set();
  const clamp = (v, min, max, fallback) => Number.isFinite(Number(v)) ? Math.min(max, Math.max(min, Number(v))) : fallback;
  function normalize(v = {}) { return {
    enabled: v.enabled !== false,
    inputGain: clamp(v.inputGain, 0, 200, defaults.inputGain), highPass: clamp(v.highPass, 20, 300, defaults.highPass),
    lowGain: clamp(v.lowGain, -12, 12, 0), midGain: clamp(v.midGain, -12, 12, 0), highGain: clamp(v.highGain, -12, 12, 0),
    compressor: v.compressor !== false, threshold: clamp(v.threshold, -60, 0, -24), ratio: clamp(v.ratio, 1, 20, 3),
    noiseSuppression: v.noiseSuppression !== false, echoCancellation: v.echoCancellation !== false, autoGainControl: !!v.autoGainControl
  }; }
  function load() { try { settings = normalize(JSON.parse(localStorage.getItem('livestudio.audio.settings') || '{}')); } catch { settings = { ...defaults }; } return settings; }
  function applyGraph(graph) {
    if (!graph) return;
    const set = (node, key, value) => { try { node[key].setTargetAtTime(value, node.context.currentTime, 0.025); } catch { try { node[key].value = value; } catch {} } };
    set(graph.input, 'gain', settings.enabled ? settings.inputGain / 100 : 1);
    set(graph.hp, 'frequency', settings.highPass);
    set(graph.low, 'gain', settings.lowGain); set(graph.mid, 'gain', settings.midGain); set(graph.high, 'gain', settings.highGain);
    set(graph.compressor, 'threshold', settings.threshold); set(graph.compressor, 'ratio', settings.compressor ? settings.ratio : 1);
  }
  function save(v) {
    settings = normalize(v); localStorage.setItem('livestudio.audio.settings', JSON.stringify(settings));
    activeGraphs.forEach(applyGraph); if (testGraph?.graph) applyGraph(testGraph.graph);
    return settings;
  }
  function buildGraph(context, source) {
    const input = context.createGain(); input.gain.value = settings.enabled ? settings.inputGain / 100 : 1;
    const hp = context.createBiquadFilter(); hp.type = 'highpass'; hp.frequency.value = settings.highPass; hp.Q.value = .707;
    const low = context.createBiquadFilter(); low.type = 'lowshelf'; low.frequency.value = 120; low.gain.value = settings.lowGain;
    const mid = context.createBiquadFilter(); mid.type = 'peaking'; mid.frequency.value = 1200; mid.Q.value = 0.9; mid.gain.value = settings.midGain;
    const high = context.createBiquadFilter(); high.type = 'highshelf'; high.frequency.value = 6500; high.gain.value = settings.highGain;
    const compressor = context.createDynamicsCompressor(); compressor.threshold.value = settings.threshold; compressor.knee.value = 18; compressor.ratio.value = settings.ratio; compressor.attack.value = .008; compressor.release.value = .18;
    const analyser = context.createAnalyser(); analyser.fftSize = 512;
    const silentMonitor = context.createGain(); silentMonitor.gain.value = 0;
    analyser.connect(silentMonitor); silentMonitor.connect(context.destination);
    source.connect(input); input.connect(hp); hp.connect(low); low.connect(mid); mid.connect(high);
    high.connect(compressor);
    const tail = compressor;
    if (!settings.compressor) compressor.ratio.value = 1;
    tail.connect(analyser);
    return { input, hp, low, mid, high, compressor, analyser, tail, context };
  }
  async function createProcessedTrack(track) {
    if (!track || track.readyState !== 'live') throw new Error('ترک میکروفون فعال نیست.');
    if (!settings.enabled) return { track, cleanup() {} };
    const context = new AudioContext();
    await context.resume();
    const source = context.createMediaStreamSource(new MediaStream([track]));
    const graph = buildGraph(context, source);
    activeGraphs.add(graph);
    const destination = context.createMediaStreamDestination();
    graph.analyser.connect(destination);
    return { track: destination.stream.getAudioTracks()[0], cleanup: async () => { activeGraphs.delete(graph); try { source.disconnect(); destination.stream.getTracks().forEach(t => t.stop()); await context.close(); } catch {} } };
  }
  async function startTest(deviceId) {
    await stopTest();
    const audio = { echoCancellation: settings.echoCancellation, noiseSuppression: settings.noiseSuppression, autoGainControl: settings.autoGainControl };
    if (deviceId) audio.deviceId = { exact: deviceId };
    testStream = await navigator.mediaDevices.getUserMedia({ audio, video: false });
    const context = new AudioContext(); await context.resume();
    const source = context.createMediaStreamSource(testStream); const graph = buildGraph(context, source);
    testGraph = { context, source, graph }; activeGraphs.add(graph);
    const meter = document.getElementById('audioInputMeter');
    const level = document.getElementById('audioInputLevel');
    const data = new Uint8Array(graph.analyser.fftSize);
    const tick = () => {
      if (!testGraph) return;
      graph.analyser.getByteTimeDomainData(data);
      let sum = 0; for (const x of data) { const n = (x - 128) / 128; sum += n * n; }
      const rms = Math.sqrt(sum / data.length), pct = Math.min(100, Math.round(rms * 260));
      if (meter) meter.style.width = pct + '%'; if (level) level.textContent = pct + '%';
      meterFrame = requestAnimationFrame(tick);
    };
    tick();
    return { label: testStream.getAudioTracks()[0]?.label || 'میکروفون فعال', settings: testStream.getAudioTracks()[0]?.getSettings?.() || {} };
  }
  async function stopTest() {
    if (meterFrame) cancelAnimationFrame(meterFrame); meterFrame = 0;
    if (testStream) testStream.getTracks().forEach(t => t.stop()); testStream = null;
    if (testGraph) { activeGraphs.delete(testGraph.graph); try { testGraph.source.disconnect(); await testGraph.context.close(); } catch {} testGraph = null; }
    const meter = document.getElementById('audioInputMeter'); if (meter) meter.style.width = '0%';
  }
  async function devices() { const list = await navigator.mediaDevices.enumerateDevices(); return list.filter(d => d.kind === 'audioinput').map(d => ({ deviceId: d.deviceId, label: d.label || 'میکروفون' })); }
  window.audioMixer = { defaults, load, save, devices, startTest, stopTest, createProcessedTrack, applyActive: () => activeGraphs.forEach(applyGraph) };
  load();
  window.addEventListener('pagehide', () => { stopTest(); });
})();
