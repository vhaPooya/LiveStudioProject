(() => {
  const DB_NAME = 'LiveStudioMediaBox';
  const STORE = 'assets';
  const urls = new Map();
  let dbPromise;
  const openDb = () => {
    if (dbPromise) return dbPromise;
    dbPromise = new Promise((resolve, reject) => {
      const req = indexedDB.open(DB_NAME, 1);
      req.onupgradeneeded = () => {
        const db = req.result;
        if (!db.objectStoreNames.contains(STORE)) db.createObjectStore(STORE, { keyPath: 'id' });
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error || new Error('IndexedDB unavailable'));
    });
    return dbPromise;
  };
  const transaction = async (mode, action) => {
    const db = await openDb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, mode);
      const store = tx.objectStore(STORE);
      let result;
      try { result = action(store); } catch (e) { reject(e); return; }
      tx.oncomplete = () => resolve(result?.result);
      tx.onerror = () => reject(tx.error || new Error('Media storage transaction failed'));
      tx.onabort = () => reject(tx.error || new Error('Media storage transaction aborted'));
    });
  };
  const demoAssets = [
    ['قاب شفاف برنامه','image/svg+xml','overlay','/media-demo/transparent-frame.svg'],
    ['پس‌زمینه اتاق خبر','image/svg+xml','image-bg','/media-demo/newsroom-bg.svg'],
    ['لوگوی نمونه کانال','image/svg+xml','logo','/media-demo/demo-logo.svg'],
    ['پس‌زمینه ویدئویی حلقه‌ای','video/mp4','video-bg','/media-demo/demo-loop.mp4'],
    ['وله آغاز برنامه','video/mp4','stinger','/media-demo/demo-stinger.mp4'],
    ['افکت صوتی نمونه','audio/wav','audio','/media-demo/demo-tone.wav']
  ];
  const seedDemos = async db => {
    const count = await new Promise((resolve, reject) => { const r=db.transaction(STORE,'readonly').objectStore(STORE).count(); r.onsuccess=()=>resolve(r.result); r.onerror=()=>reject(r.error); });
    if (count) return;
    for (const [name,mimeType,category,path] of demoAssets) {
      const response = await fetch(path); if (!response.ok) continue;
      const blob = await response.blob(); const item={id:'demo-'+category,name,mimeType,category,size:blob.size,blob,createdAt:new Date().toISOString()};
      await new Promise((resolve,reject)=>{const tx=db.transaction(STORE,'readwrite');tx.objectStore(STORE).put(item);tx.oncomplete=resolve;tx.onerror=()=>reject(tx.error);});
    }
  };
  const toDto = item => {
    if (urls.has(item.id)) URL.revokeObjectURL(urls.get(item.id));
    const url = URL.createObjectURL(item.blob);
    urls.set(item.id, url);
    return { id: item.id, name: item.name, mimeType: item.mimeType, category: item.category, size: item.size, url, createdAt: item.createdAt };
  };

  // ---- Server (images/audio) and Google Drive (videos) storage; local IndexedDB stays for demos and as fallback for videos
  const HDR = { 'X-LS-Upload': '1' };
  const isRemoteId = id => /^[0-9a-f]{8}-[0-9a-f]{4}-/i.test(id || '');
  const apiJson = async (url, init) => {
    const r = await fetch(url, { credentials: 'same-origin', ...init });
    let body = null; try { body = await r.json(); } catch { /* empty */ }
    if (!r.ok) throw new Error(body?.error || ('خطا ' + r.status));
    return body;
  };
  let driveEnabled = false;
  const remoteDto = x => ({ id: x.id, name: x.name, mimeType: x.mimeType, category: x.category, size: x.size, url: '/media-api/files/' + x.id, createdAt: x.createdAt, storage: x.storage });
  const fetchRemote = async () => {
    try {
      const data = await apiJson('/media-api/list');
      driveEnabled = !!data.drive?.enabled;
      return (data.items || []).map(remoteDto);
    } catch { return []; }
  };
  const uploadToDrive = async (blob, name, mimeType, category) => {
    const sess = await apiJson('/media-api/drive/session', { method: 'POST', headers: { ...HDR, 'Content-Type': 'application/json' }, body: JSON.stringify({ name, mimeType, size: blob.size }) });
    const CHUNK = 8 * 1024 * 1024; // multiple of 256 KiB
    let offset = 0, fileId = null;
    while (offset < blob.size) {
      const end = Math.min(offset + CHUNK, blob.size);
      const r = await fetch(sess.url, { method: 'PUT', headers: { 'Content-Range': `bytes ${offset}-${end - 1}/${blob.size}` }, body: blob.slice(offset, end) });
      if (r.status === 308) { offset = end; continue; }
      if (r.ok) { try { fileId = (await r.json()).id; } catch { /* empty */ } offset = end; break; }
      throw new Error('آپلود در Google Drive ناموفق بود (' + r.status + ').');
    }
    if (!fileId) throw new Error('شناسه فایل از Google Drive دریافت نشد.');
    return apiJson('/media-api/drive/register', { method: 'POST', headers: { ...HDR, 'Content-Type': 'application/json' }, body: JSON.stringify({ fileId, name, category }) });
  };

  const previewTimers = new WeakMap();
  const readAsset = async id => {
    if (!id) return null;
    const db = await openDb();
    const local = await new Promise((resolve, reject) => { const r=db.transaction(STORE,'readonly').objectStore(STORE).get(id); r.onsuccess=()=>resolve(r.result || null); r.onerror=()=>reject(r.error); });
    if (local || !isRemoteId(id)) return local;
    try { const r = await fetch('/media-api/files/' + id, { credentials: 'same-origin' }); if (!r.ok) return null; const blob = await r.blob(); return { id, mimeType: blob.type, blob }; } catch { return null; }
  };
  const animationClass = value => ({float:'logo-float',pulse:'logo-pulse',rotate:'logo-rotate',glow:'logo-glow'})[value] || '';
  const attachPreview = async selector => {
    const host = document.querySelector(selector); if (!host) return;
    host.querySelectorAll('.ls-shared-layer,.ls-shared-background').forEach(x=>x.remove());
    host.classList.remove('ls-has-background');
    const cfg = window.mediaBox.getDesign(); if (!cfg) return;
    const layer = document.createElement('div'); layer.className='ls-shared-layer';
    const bg = await readAsset(cfg.backgroundId);
    if (bg && (bg.mimeType.startsWith('image/') || bg.mimeType.startsWith('video/'))) host.classList.add('ls-has-background');
    if (bg && bg.mimeType.startsWith('image/')) { const url=URL.createObjectURL(bg.blob); const el=document.createElement('div'); el.className='ls-shared-background'; el.style.backgroundImage=`linear-gradient(#07101a22,#07101a22),url("${url}")`; host.prepend(el); }
    else if (bg && bg.mimeType.startsWith('video/')) { const url=URL.createObjectURL(bg.blob); const el=document.createElement('video'); el.className='ls-shared-background'; el.src=url; el.autoplay=true; el.loop=true; el.muted=true; el.playsInline=true; host.prepend(el); }
    const logo = await readAsset(cfg.logoId);
    if (logo) { const img=document.createElement('img'); img.className=`ls-shared-logo ${animationClass(cfg.logoAnimation)}`; img.src=URL.createObjectURL(logo.blob); img.style.left=`${cfg.logoX}%`; img.style.top=`${cfg.logoY}%`; img.style.width=`${cfg.logoSize}%`; layer.appendChild(img); }
    if (cfg.showLowerThird) { const box=document.createElement('div'); box.className='ls-shared-lower-third'; const title=document.createElement('strong'); title.textContent=cfg.lowerThirdTitle || ''; const sub=document.createElement('span'); sub.textContent=cfg.lowerThirdSubtitle || ''; box.append(title,sub); layer.appendChild(box); }
    if (cfg.showBreaking) { const bar=document.createElement('div'); bar.className='ls-shared-breaking'; const tag=document.createElement('b'); tag.textContent='BREAKING NEWS'; const msg=document.createElement('span'); msg.textContent=cfg.breakingText || ''; const clock=document.createElement('time'); bar.append(tag,msg,clock); layer.appendChild(bar); const update=()=>clock.textContent=new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'}); update(); const timer=setInterval(update,1000); previewTimers.set(host,timer); }
    host.appendChild(layer);
  };
  window.mediaBox = {
    async list() {
      const db = await openDb();
      await seedDemos(db);
      const rows = await new Promise((resolve, reject) => {
        const req = db.transaction(STORE, 'readonly').objectStore(STORE).getAll();
        req.onsuccess = () => resolve(req.result || []);
        req.onerror = () => reject(req.error);
      });
      const remote = await fetchRemote();
      return [...remote, ...rows.map(toDto)].sort((a,b) => new Date(b.createdAt) - new Date(a.createdAt));
    },
    async add(name, mimeType, category, size, streamRef) {
      const buffer = await streamRef.arrayBuffer();
      const blob = new Blob([buffer], { type: mimeType });
      const isVideo = (mimeType || '').startsWith('video/');
      if (!isVideo) {
        const r = await apiJson('/media-api/upload?category=' + encodeURIComponent(category), { method: 'POST', headers: { ...HDR, 'X-LS-Name': encodeURIComponent(name), 'Content-Type': 'application/octet-stream' }, body: blob });
        return remoteDto(r);
      }
      await fetchRemote();
      if (driveEnabled) return remoteDto(await uploadToDrive(blob, name, mimeType, category));
      const item = { id: crypto.randomUUID(), name, mimeType, category, size, blob, createdAt: new Date().toISOString() };
      await transaction('readwrite', store => store.put(item));
      return toDto(item);
    },
    async remove(id) {
      if (isRemoteId(id)) { await apiJson('/media-api/' + id, { method: 'DELETE', headers: HDR }).catch(() => {}); return; }
      await transaction('readwrite', store => store.delete(id));
      if (urls.has(id)) { URL.revokeObjectURL(urls.get(id)); urls.delete(id); }
    },
    saveDesign(config) { localStorage.setItem('livestudio.sharedSceneDesign', JSON.stringify(config)); },
    getEffects() { try { const value = JSON.parse(localStorage.getItem('livestudio.sceneEffects') || 'null'); return window.lsSceneEffects?.set(value || window.lsSceneEffects.defaults) || value; } catch { return window.lsSceneEffects?.defaults || null; } },
    saveEffects(config) { const value = window.lsSceneEffects?.set(config) || config; localStorage.setItem('livestudio.sceneEffects', JSON.stringify(value)); return value; },
    async attachPreviews() { await attachPreview('.studio-preview'); await attachPreview('.creator-preview'); },
    async attachPreview(selector) { await attachPreview(selector); },
    startClock() { const update=()=>document.querySelectorAll('.media-stage-clock').forEach(el=>el.textContent=new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'})); update(); if (window.__lsMediaClock) clearInterval(window.__lsMediaClock); window.__lsMediaClock=setInterval(update,1000); },
    getDesign() {
      try { return JSON.parse(localStorage.getItem('livestudio.sharedSceneDesign') || 'null'); }
      catch { return null; }
    },
    releaseUrls() {
      if (window.__lsMediaClock) clearInterval(window.__lsMediaClock);
      for (const url of urls.values()) URL.revokeObjectURL(url);
      urls.clear();
    }
  };
})();
