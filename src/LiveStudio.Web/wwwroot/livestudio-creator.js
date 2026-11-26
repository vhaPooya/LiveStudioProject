(() => {
    let stream = null;
    let recorder = null;
    let chunks = [];
    let preview = null;
    let currentMime = "video/webm";
    let outputCanvas = null;
    let outputContext = null;
    let outputFrame = 0;
    let outputStream = null;
    let sceneAssets = [];
    let sceneDesign = null;
    let sceneImage = null;
    let sceneVideo = null;
    let sceneLogo = null;
    let sceneStartedAt = 0;
    let processedAudioCleanup = null;
    let recordingStartedAt = 0;
    let pausedDuration = 0;
    let pauseStartedAt = 0;
    let recordingClock = 0;

    const loadImage = url => new Promise(resolve => {
        if (!url) return resolve(null);
        const img = new Image(); img.onload = () => resolve(img); img.onerror = () => resolve(null); img.src = url;
    });
    const cover = (ctx, source, width, height) => {
        const sw = source.videoWidth || source.naturalWidth || source.width;
        const sh = source.videoHeight || source.naturalHeight || source.height;
        if (!sw || !sh) return;
        const scale = Math.max(width / sw, height / sh);
        const w = sw * scale, h = sh * scale;
        ctx.drawImage(source, (width - w) / 2, (height - h) / 2, w, h);
    };
    async function prepareProgramOutput() {
        outputCanvas = document.createElement('canvas');
        outputCanvas.width = 1920; outputCanvas.height = 1080;
        outputContext = outputCanvas.getContext('2d', { alpha: false });
        window.mediaBox?.getEffects?.();
        sceneDesign = window.mediaBox?.getDesign?.() || null;
        sceneAssets = await (window.mediaBox?.list?.() || []);
        const find = id => sceneAssets.find(x => x.id === id);
        const bg = find(sceneDesign?.backgroundId);
        const logo = find(sceneDesign?.logoId);
        sceneImage = bg?.mimeType?.startsWith('image/') ? await loadImage(bg.url) : null;
        sceneVideo = null;
        if (bg?.mimeType?.startsWith('video/')) {
            sceneVideo = document.createElement('video'); sceneVideo.src = bg.url;
            sceneVideo.loop = true; sceneVideo.muted = true; sceneVideo.playsInline = true;
            try { await sceneVideo.play(); } catch (_) { }
        }
        sceneLogo = logo ? await loadImage(logo.url) : null;
        sceneStartedAt = performance.now();
        const draw = now => {
            if (!outputContext || !stream) return;
            const ctx = outputContext, w = outputCanvas.width, h = outputCanvas.height;
            ctx.fillStyle = '#101827'; ctx.fillRect(0, 0, w, h);
            if (sceneVideo?.readyState >= 2) cover(ctx, sceneVideo, w, h);
            else if (sceneImage) cover(ctx, sceneImage, w, h);
            else { const g=ctx.createLinearGradient(0,0,w,h); g.addColorStop(0,'#14243a'); g.addColorStop(1,'#050a12'); ctx.fillStyle=g; ctx.fillRect(0,0,w,h); }
            if (preview?.readyState >= 2) {
                if (sceneImage || sceneVideo) {
                    const pw = w * .30, ph = h * .30, px = w - pw - 54, py = h - ph - 54;
                    ctx.save(); ctx.shadowColor='rgba(0,0,0,.65)'; ctx.shadowBlur=28;
                    ctx.fillStyle='#fff'; ctx.fillRect(px-7,py-7,pw+14,ph+14);
                    ctx.beginPath(); ctx.rect(px,py,pw,ph); ctx.clip(); (window.lsSceneEffects?.drawCover || cover)(ctx, preview, pw, ph); ctx.restore();
                } else (window.lsSceneEffects?.drawCover || cover)(ctx, preview, w, h);
            }
            if (sceneDesign?.logoId && sceneLogo) {
                const size = Math.max(4, Math.min(40, Number(sceneDesign.logoSize) || 14)) / 100 * w;
                const x = (Number(sceneDesign.logoX) || 4) / 100 * w;
                let y = (Number(sceneDesign.logoY) || 4) / 100 * h;
                const anim = sceneDesign.logoAnimation || 'none';
                const t = (now - sceneStartedAt) / 1000;
                let alpha=1, scale=1, rotation=0;
                if (anim==='pulse') scale=1 + .06*Math.sin(t*3);
                if (anim==='float') y += Math.sin(t*2.2)*14;
                if (anim==='glow') alpha=.82 + .18*Math.sin(t*4);
                if (anim==='rotate') rotation=(t%5)*Math.PI*2/5;
                const ratio=sceneLogo.naturalHeight/sceneLogo.naturalWidth;
                const dw=size, dh=size*ratio;
                ctx.save(); ctx.globalAlpha=alpha; ctx.translate(x+dw/2,y+dh/2); ctx.rotate(rotation); ctx.scale(scale,scale);
                ctx.shadowColor=anim==='glow'?'#72d7ff':'rgba(0,0,0,.65)'; ctx.shadowBlur=anim==='glow'?32:14;
                ctx.drawImage(sceneLogo,-dw/2,-dh/2,dw,dh); ctx.restore();
            }
            if (sceneDesign?.showLowerThird) {
                const bottom=h*.79; ctx.fillStyle='rgba(12,29,53,.92)'; ctx.fillRect(w*.04,bottom,w*.72,h*.115);
                ctx.fillStyle='#55c8ff'; ctx.fillRect(w*.04,bottom,12,h*.115);
                ctx.textAlign='right'; ctx.direction='rtl'; ctx.fillStyle='#fff'; ctx.font='bold 42px Arial';
                ctx.fillText(sceneDesign.lowerThirdTitle || '',w*.73,bottom+48);
                ctx.fillStyle='#c7d8eb'; ctx.font='28px Arial'; ctx.fillText(sceneDesign.lowerThirdSubtitle || '',w*.73,bottom+88);
            }
            if (sceneDesign?.showBreaking) {
                const bh=88, by=h-bh; ctx.fillStyle='#a91f36'; ctx.fillRect(0,by,w,bh);
                ctx.fillStyle='#fff'; ctx.fillRect(w-310,by+13,280,62);
                ctx.textAlign='center'; ctx.direction='rtl'; ctx.fillStyle='#9e1830'; ctx.font='bold 27px Arial'; ctx.fillText('خبر فوری',w-170,by+53);
                ctx.textAlign='right'; ctx.fillStyle='#fff'; ctx.font='bold 30px Arial'; ctx.fillText(sceneDesign.breakingText || '',w-340,by+55, w-500);
                ctx.textAlign='left'; ctx.font='28px Arial'; ctx.fillText(new Date().toLocaleTimeString([], {hour:'2-digit',minute:'2-digit'}),w-145,by+55);
            }
            outputFrame=requestAnimationFrame(draw);
        };
        cancelAnimationFrame(outputFrame); outputFrame=requestAnimationFrame(draw);
    }
    function stopProgramOutput() {
        if (outputFrame) cancelAnimationFrame(outputFrame);
        outputFrame=0;
        if (sceneVideo) { sceneVideo.pause(); sceneVideo.src=''; sceneVideo=null; }
        if (outputStream) { outputStream.getTracks().forEach(t=>t.stop()); outputStream=null; }
        outputCanvas=null; outputContext=null; sceneImage=null; sceneLogo=null; sceneAssets=[];
    }

    const chooseMime = () => {
        const candidates = ["video/webm;codecs=vp9,opus", "video/webm;codecs=vp8,opus", "video/webm"];
        return candidates.find(x => window.MediaRecorder && MediaRecorder.isTypeSupported(x)) || "";
    };

    async function acquire(cameraId, micId, quality) {
        const video = { width: { ideal: quality === 720 ? 1280 : 1920 }, height: { ideal: quality === 720 ? 720 : 1080 }, frameRate: { ideal: 30, max: 30 } };
        if (cameraId) video.deviceId = { exact: cameraId };
        const audioProfile = window.audioMixer?.load?.() || {};
        const audio = micId ? { deviceId: { exact: micId }, echoCancellation: audioProfile.echoCancellation !== false, noiseSuppression: audioProfile.noiseSuppression !== false, autoGainControl: !!audioProfile.autoGainControl }
                            : { echoCancellation: audioProfile.echoCancellation !== false, noiseSuppression: audioProfile.noiseSuppression !== false, autoGainControl: !!audioProfile.autoGainControl };
        const next = await navigator.mediaDevices.getUserMedia({ video, audio });
        if (stream) stream.getTracks().forEach(t => t.stop());
        stream = next;
        if (preview) { preview.srcObject = stream; await preview.play(); }
        const placeholder = document.getElementById("creatorPlaceholder");
        if (placeholder) placeholder.style.display = "none";
        return stream;
    }

    window.creatorStudio = {
        async prepare(videoId, quality) {
            if (!navigator.mediaDevices?.getUserMedia) throw new Error("مرورگر از دسترسی دوربین پشتیبانی نمی‌کند یا صفحه در بستر امن اجرا نشده است.");
            preview = document.getElementById(videoId);
            if (!preview) throw new Error("عنصر پیش‌نمایش پیدا نشد.");
            await acquire(null, null, quality);
        },
        async devices() {
            const all = await navigator.mediaDevices.enumerateDevices();
            return {
                cameras: all.filter(x => x.kind === "videoinput").map(x => ({ deviceId: x.deviceId, label: x.label || "دوربین" })),
                microphones: all.filter(x => x.kind === "audioinput").map(x => ({ deviceId: x.deviceId, label: x.label || "میکروفون" }))
            };
        },
        async switchDevices(cameraId, micId, quality) { await acquire(cameraId || null, micId || null, quality); },
        async startRecording(fileName, category, quality) {
            if (!stream) throw new Error("ابتدا دوربین و میکروفون را فعال کنید.");
            if (!window.MediaRecorder) throw new Error("ضبط ویدئو در این مرورگر پشتیبانی نمی‌شود.");
            chunks = [];
            const mime = chooseMime();
            currentMime = mime || "video/webm";
            {
                const canvasStream = await window.studioCompositor?.outputStream?.('creator');
                if (!canvasStream) throw new Error("موتور ترکیب تصویر آماده نیست. پنل استودیوی تولید محتوا را تازه‌سازی کنید.");
                const sourceAudio = stream.getAudioTracks()[0];
                if (sourceAudio && window.audioMixer?.createProcessedTrack) {
                    const processed = await window.audioMixer.createProcessedTrack(sourceAudio);
                    processedAudioCleanup = processed.cleanup;
                    canvasStream.addTrack(processed.track);
                } else if (sourceAudio) canvasStream.addTrack(sourceAudio);
                outputStream = canvasStream;
                const videoBitrate = Number(quality) === 720 ? 4500000 : 8500000;
                recorder = new MediaRecorder(canvasStream, mime ? { mimeType: mime, videoBitsPerSecond: videoBitrate, audioBitsPerSecond: 192000 } : { videoBitsPerSecond: videoBitrate, audioBitsPerSecond: 192000 });
                recorder.ondataavailable = e => { if (e.data && e.data.size) chunks.push(e.data); };
                recorder.start(1000);
                recordingStartedAt = performance.now(); pausedDuration = 0; pauseStartedAt = 0;
                const tag = document.getElementById("creatorRecTag");
                if (tag) { tag.hidden = false; tag.textContent = "● ضبط 00:00"; }
                if (recordingClock) clearInterval(recordingClock);
                recordingClock = setInterval(() => {
                    if (!recorder || recorder.state === "inactive") return;
                    const elapsed = Math.max(0, performance.now() - recordingStartedAt - pausedDuration - (pauseStartedAt ? performance.now() - pauseStartedAt : 0));
                    const seconds = Math.floor(elapsed / 1000);
                    const label = `${String(Math.floor(seconds / 60)).padStart(2,'0')}:${String(seconds % 60).padStart(2,'0')}`;
                    const clock = document.getElementById("creatorRecTag");
                    if (clock) clock.textContent = `${recorder.state === "paused" ? "Ⅱ مکث" : "● ضبط"} ${label}`;
                }, 500);
                window.__creatorRecordingName = (fileName || "LiveStudio-Recording") + "-" + (category || "video");
            }
            return;
        },
        pauseRecording() {
            if (!recorder || recorder.state !== "recording") throw new Error("ضبط در حال اجرا نیست.");
            recorder.pause(); pauseStartedAt = performance.now();
            return { state: recorder.state };
        },
        resumeRecording() {
            if (!recorder || recorder.state !== "paused") throw new Error("ضبط در حالت مکث نیست.");
            if (pauseStartedAt) pausedDuration += performance.now() - pauseStartedAt;
            pauseStartedAt = 0; recorder.resume();
            return { state: recorder.state };
        },
        recordingStatus() {
            if (!recorder) return { state: "inactive", elapsedMilliseconds: 0 };
            const elapsed = Math.max(0, performance.now() - recordingStartedAt - pausedDuration - (pauseStartedAt ? performance.now() - pauseStartedAt : 0));
            return { state: recorder.state, elapsedMilliseconds: Math.floor(elapsed) };
        },
        stopRecording() {
            return new Promise((resolve, reject) => {
                if (!recorder || recorder.state === "inactive") { reject(new Error("ضبط فعالی وجود ندارد.")); return; }
                recorder.onerror = e => reject(e.error || new Error("خطا در ضبط ویدئو."));
                recorder.onstop = async () => {
                    const blob = new Blob(chunks, { type: currentMime });
                    if (recordingClock) { clearInterval(recordingClock); recordingClock = 0; }
                    const elapsed = Math.max(0, performance.now() - recordingStartedAt - pausedDuration - (pauseStartedAt ? performance.now() - pauseStartedAt : 0));
                    const url = URL.createObjectURL(blob);
                    const a = document.createElement("a");
                    a.href = url;
                    const extension = blob.type.includes("mp4") ? "mp4" : blob.type.includes("ogg") ? "ogv" : "webm";
                    a.download = (window.__creatorRecordingName || "LiveStudio-Recording") + "." + extension;
                    document.body.appendChild(a); a.click(); a.remove();
                    setTimeout(() => URL.revokeObjectURL(url), 60000);
                    chunks = []; recorder = null; recordingStartedAt = 0; pausedDuration = 0; pauseStartedAt = 0;
                    stopProgramOutput();
                    if (processedAudioCleanup) { try { await processedAudioCleanup(); } catch (_) { } processedAudioCleanup = null; }
                    const tag = document.getElementById("creatorRecTag");
                    if (tag) tag.hidden = true;
                    resolve({ size: blob.size, mimeType: blob.type, durationMilliseconds: Math.floor(elapsed) });
                };
                recorder.stop();
            });
        },
        stop() {
            if (recorder && recorder.state !== "inactive") recorder.stop();
            if (recordingClock) { clearInterval(recordingClock); recordingClock = 0; }
            stopProgramOutput();
            if (processedAudioCleanup) { try { processedAudioCleanup(); } catch (_) { } processedAudioCleanup = null; }
            if (stream) stream.getTracks().forEach(t => t.stop());
            stream = null;
            if (preview) { preview.pause(); preview.srcObject = null; }
            const placeholder = document.getElementById("creatorPlaceholder");
            if (placeholder) placeholder.style.display = "flex";
            const tag = document.getElementById("creatorRecTag");
            if (tag) tag.hidden = true;
        },
        dispose() {
            if (recorder && recorder.state !== "inactive") {
                recorder.onstop = null;
                recorder.onerror = null;
                try { recorder.stop(); } catch (_) { }
            }
            recorder = null; chunks = [];
            if (recordingClock) { clearInterval(recordingClock); recordingClock = 0; }
            stopProgramOutput();
            if (processedAudioCleanup) { try { processedAudioCleanup(); } catch (_) { } processedAudioCleanup = null; }
            if (stream) stream.getTracks().forEach(t => t.stop());
            stream = null;
            if (preview) { preview.pause(); preview.srcObject = null; }
        }
    };
})();

// Independent smart teleprompter engine
(() => {
    let frame = 0, running = false, last = 0, speed = 45, mirrored = false;
    const stage = () => document.getElementById('teleprompterStage');
    const reading = () => document.getElementById('teleprompterReading');
    const state = () => document.getElementById('tpState');
    function tick(now) {
        if (!running) return;
        if (last) {
            const elapsed = Math.min((now - last) / 1000, 0.05);
            const el = stage();
            if (el) {
                el.scrollTop += speed * elapsed;
                if (el.scrollTop + el.clientHeight >= el.scrollHeight - 2) {
                    running = false;
                    if (state()) state().textContent = 'پایان متن';
                    return;
                }
            }
        }
        last = now;
        frame = requestAnimationFrame(tick);
    }
    function setRunning(value, nextSpeed) {
        speed = Number(nextSpeed) || speed;
        running = !!value;
        last = 0;
        if (frame) cancelAnimationFrame(frame);
        if (running) {
            if (state()) state().textContent = 'در حال حرکت';
            frame = requestAnimationFrame(tick);
        } else if (state()) state().textContent = 'متوقف';
    }
    window.liveStudioTeleprompter = {
        setText(text, nextSpeed, fontSize, lineSpacing) {
            const el = reading();
            if (!el) return;
            el.textContent = text || 'متن سناریو در این قسمت نمایش داده می‌شود.';
            speed = Number(nextSpeed) || 45;
            el.style.fontSize = `${fontSize || 36}px`;
            el.style.lineHeight = String(lineSpacing || 2);
        },
        toggle: setRunning,
        reset() {
            setRunning(false, speed);
            const el = stage(); if (el) el.scrollTop = 0;
        },
        mirror(value) {
            mirrored = !!value;
            const el = reading(); if (el) el.style.transform = mirrored ? 'scaleX(-1)' : '';
        },
        async fullscreen() {
            const el = stage(); if (!el) return;
            try {
                if (!document.fullscreenElement) await el.requestFullscreen();
                else await document.exitFullscreen();
            } catch (_) { }
        },
        async copy(text) {
            try { await navigator.clipboard.writeText(text || ''); }
            catch (_) {
                const area = document.createElement('textarea'); area.value = text || ''; document.body.appendChild(area); area.select(); document.execCommand('copy'); area.remove();
            }
        },
        dispose() { setRunning(false, speed); }
    };
    document.addEventListener('keydown', e => {
        if (!window.location.pathname.toLowerCase().startsWith('/teleprompter')) return;
        if (e.target && ['INPUT', 'TEXTAREA'].includes(e.target.tagName)) return;
        if (e.code === 'Space') { e.preventDefault(); setRunning(!running, speed); }
        else if (e.key === 'ArrowUp') { speed = Math.min(180, speed + 5); }
        else if (e.key === 'ArrowDown') { speed = Math.max(10, speed - 5); }
        else if (e.key === 'Home') { const el = stage(); if (el) el.scrollTop = 0; setRunning(false, speed); }
    });
})();
