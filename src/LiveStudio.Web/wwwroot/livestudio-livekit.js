(() => {
    let room = null;
    let chatHandler = null;
    const isInfra = p => /^(EG_|egress|agent)/i.test(p?.identity || '');
    const roster = () => room ? [...room.remoteParticipants.values()].filter(p => !isInfra(p)).map(p => ({ identity: p.identity, name: p.name || p.identity })) : [];
    const notifyRoster = () => { try { chatHandler?.invokeMethodAsync('OnChatRoster', JSON.stringify({ me: room?.localParticipant?.name || room?.localParticipant?.identity || '', people: roster() })); } catch (_) { } };
    let cameraTrack = null;
    let screenTrack = null;
    let programCanvas = null;
    let programContext = null;
    let programFrame = 0;
    let programVideo = null;
    let programCameraMediaTrack = null;
    let programPublishedTrack = null;
    let programAssets = [];
    let programBackground = null;
    let programLogo = null;
    let programBackgroundVideo = null;
    let programActive = false;
    let liveRawAudioTrack = null;
    let liveProcessedPublication = null;
    let liveProcessedCleanup = null;
    let statusHandler = null;
    let deviceChangeHandler = null;

    const notifyStatus = (state, message = '') => {
        try { statusHandler?.invokeMethodAsync('OnLiveKitStatusChanged', state, message); }
        catch (error) { console.debug('LiveStudio status callback unavailable:', error); }
    };
    const notifyDevicesChanged = () => {
        try { statusHandler?.invokeMethodAsync('OnLiveKitDevicesChanged'); }
        catch (error) { console.debug('LiveStudio device callback unavailable:', error); }
    };

    const cameraElement = (id = 'cameraPreview') => document.getElementById(id);
    const showPreview = (visible) => {
        const video = cameraElement();
        const placeholder = document.getElementById('cameraPlaceholder');
        if (video) video.style.display = visible ? 'block' : 'none';
        if (placeholder) placeholder.style.display = visible ? 'none' : 'flex';
    };

    const attachVideo = (track, element) => {
        if (!track || !element) return;
        track.attach(element);
        element.autoplay = true;
        element.playsInline = true;
        element.muted = true;
        showPreview(true);
    };

    const requireRoom = () => {
        if (!room || room.state !== 'connected') throw new Error('ابتدا به اتاق LiveKit متصل شوید.');
        return room;
    };

    async function clearLiveProcessedAudio() {
        if (room && liveProcessedPublication?.track) {
            try { await room.localParticipant.unpublishTrack(liveProcessedPublication.track, false); } catch (_) { }
        }
        liveProcessedPublication = null;
        if (liveProcessedCleanup) { try { await liveProcessedCleanup(); } catch (_) { } liveProcessedCleanup = null; }
    }
    async function applyLiveProcessedAudio() {
        if (!room || room.state !== 'connected') return false;
        const current = room;
        const publication = current.localParticipant.getTrackPublication(window.LivekitClient.Track.Source.Microphone);
        const sourceTrack = liveRawAudioTrack || publication?.track?.mediaStreamTrack;
        if (!sourceTrack || sourceTrack.readyState !== 'live') return false;
        await clearLiveProcessedAudio();
        const processed = await window.audioMixer?.createProcessedTrack?.(sourceTrack);
        if (!processed) return false;
        if (processed.track === sourceTrack) {
            const existing = current.localParticipant.getTrackPublication(window.LivekitClient.Track.Source.Microphone);
            if (!existing?.track && sourceTrack.readyState === 'live') await current.localParticipant.publishTrack(sourceTrack, { source: window.LivekitClient.Track.Source.Microphone, name: 'microphone' });
            return false;
        }
        try {
            if (publication?.track && publication.track.mediaStreamTrack === sourceTrack)
                await current.localParticipant.unpublishTrack(publication.track, false);
            liveProcessedPublication = await current.localParticipant.publishTrack(processed.track, {
                source: window.LivekitClient.Track.Source.Microphone, name: 'livestudio-processed-microphone'
            });
            liveProcessedCleanup = processed.cleanup;
            return true;
        } catch (error) {
            try { await processed.cleanup?.(); } catch (_) { }
            try {
                const currentPub = current.localParticipant.getTrackPublication(window.LivekitClient.Track.Source.Microphone);
                if (!currentPub?.track && sourceTrack.readyState === 'live')
                    await current.localParticipant.publishTrack(sourceTrack, { source: window.LivekitClient.Track.Source.Microphone, name: 'microphone' });
            } catch (_) { }
            throw error;
        }
    }

    const loadProgramImage = url => new Promise(resolve => {
        if (!url) return resolve(null);
        const image = new Image(); image.onload = () => resolve(image); image.onerror = () => resolve(null); image.src = url;
    });
    const drawCover = (ctx, source, width, height) => {
        const sw = source.videoWidth || source.naturalWidth || source.width;
        const sh = source.videoHeight || source.naturalHeight || source.height;
        if (!sw || !sh) return;
        const scale = Math.max(width / sw, height / sh), w = sw * scale, h = sh * scale;
        ctx.drawImage(source, (width - w) / 2, (height - h) / 2, w, h);
    };
    const releaseProgramAssets = () => {
        if (programFrame) cancelAnimationFrame(programFrame);
        programFrame = 0;
        if (programBackgroundVideo) { programBackgroundVideo.pause(); programBackgroundVideo.src = ''; programBackgroundVideo = null; }
        if (programVideo) { programVideo.pause(); programVideo.srcObject = null; programVideo = null; }
        if (programCameraMediaTrack) { programCameraMediaTrack.stop(); programCameraMediaTrack = null; }
        if (programPublishedTrack) { try { programPublishedTrack.stop(); } catch (_) { } programPublishedTrack = null; }
        programCanvas = null; programContext = null; programBackground = null; programLogo = null; programAssets = [];
        programActive = false;
    };
    async function createProgramOutput() {
        const current = requireRoom();
        if (programActive) return true;
        const publication = current.localParticipant.getTrackPublication(window.LivekitClient.Track.Source.Camera);
        const localTrack = publication?.track;
        const mediaTrack = localTrack?.mediaStreamTrack;
        if (!localTrack || !mediaTrack || mediaTrack.readyState !== 'live') throw new Error('ترک دوربین فعال پیدا نشد. ابتدا دوربین را روشن کنید.');
        programCameraMediaTrack = mediaTrack.clone();
        const canvasStream = await window.studioCompositor?.outputStream?.('live');
        if (!canvasStream) throw new Error('موتور ترکیب تصویر آماده نیست. پنل پیش‌نمایش استودیو را تازه‌سازی کنید.');
        programCanvas = document.getElementById('liveComposerCanvas');
        programPublishedTrack = canvasStream.getVideoTracks()[0];
        try {
            await current.localParticipant.unpublishTrack(localTrack, false);
            await current.localParticipant.publishTrack(programPublishedTrack, { source: window.LivekitClient.Track.Source.Camera, name: 'livestudio-program-output' });
            programActive = true;
            return true;
        } catch (error) {
            try { await current.localParticipant.publishTrack(localTrack, { source: window.LivekitClient.Track.Source.Camera }); } catch (_) { }
            releaseProgramAssets();
            throw error;
        }
    }
    async function stopProgramOutput() {
        if (!room || room.state !== 'connected' || !programActive) { releaseProgramAssets(); return false; }
        const current = room, publication = current.localParticipant.getTrackPublication(window.LivekitClient.Track.Source.Camera);
        try {
            if (publication?.track) await current.localParticipant.unpublishTrack(publication.track, false);
            if (programPublishedTrack) { try { programPublishedTrack.stop(); } catch (_) { } }
            if (programCameraMediaTrack?.readyState === 'live') {
                await current.localParticipant.publishTrack(programCameraMediaTrack, { source: window.LivekitClient.Track.Source.Camera, name: 'camera' });
                cameraTrack = current.localParticipant.getTrackPublication(window.LivekitClient.Track.Source.Camera)?.track ?? null;
                programCameraMediaTrack = null;
            } else {
                const camera = await current.localParticipant.setCameraEnabled(true);
                cameraTrack = current.localParticipant.getTrackPublication(window.LivekitClient.Track.Source.Camera)?.track ?? null;
            }
        } finally { releaseProgramAssets(); }
        return true;
    }

    let sfxPublication = null;
    async function publishSfxTrack() {
        if (!room || room.state !== 'connected' || !window.lsSfx) return;
        if (sfxPublication) return;
        window.lsSfx.init();
        const track = window.lsSfx.getTrack(); if (!track) return;
        sfxPublication = await room.localParticipant.publishTrack(track, { source: window.LivekitClient.Track.Source.Unknown, name: 'livestudio-soundboard' });
    }

    window.liveStudio = {
        async enableSoundboard() { await publishSfxTrack(); return !!sfxPublication; },
        async sendControl(cmd, args) {
            const current = requireRoom();
            const msg = { cmd, args: args || {}, ts: Date.now() };
            await current.localParticipant.publishData(new TextEncoder().encode(JSON.stringify(msg)), { reliable: true, topic: 'ls-ctl' });
            return true;
        },
        async broadcastState(state) {
            if (!room || room.state !== 'connected') return false;
            await room.localParticipant.publishData(new TextEncoder().encode(JSON.stringify({ cmd: 'state', args: state || {}, ts: Date.now() })), { reliable: true, topic: 'ls-ctl' });
            return true;
        },
        identity() { return room?.localParticipant?.identity || ''; },
        // Team members: join without publishing, watch the program output and talk through the data channel.
        async connectObserver(url, token, videoId) {
            if (!window.LivekitClient) throw new Error('SDK رسمی LiveKit بارگذاری نشده است.');
            if (room) await this.disconnect();
            const { Room, RoomEvent, Track } = window.LivekitClient;
            room = new Room({ adaptiveStream: true, dynacast: true });
            room.on(RoomEvent.TrackSubscribed, (track) => {
                if (track.kind === Track.Kind.Video) { const v = document.getElementById(videoId); if (v) { track.attach(v); v.autoplay = true; v.playsInline = true; v.muted = true; } }
                else if (track.kind === Track.Kind.Audio) { const el = track.attach(); el.style.display = 'none'; el.dataset.lsAudio = '1'; document.body.appendChild(el); }
            });
            room.on(RoomEvent.TrackUnsubscribed, (track) => { track.detach().forEach(el => { if (el.dataset?.lsAudio) el.remove(); }); });
            room.on(RoomEvent.DataReceived, (payload, participant, kind, topic) => {
                if (isInfra(participant)) return;
                try {
                    if (topic === 'ls-ctl') window.lsTeam?.onControl(JSON.parse(new TextDecoder().decode(payload)), participant);
                    else if (topic === 'ls-chat') { const msg = JSON.parse(new TextDecoder().decode(payload)); msg.from = participant?.identity || ''; msg.name = msg.name || participant?.name || ''; chatHandler?.invokeMethodAsync('OnChatMessage', JSON.stringify(msg)); }
                } catch (_) { }
            });
            room.on(RoomEvent.ParticipantConnected, notifyRoster);
            room.on(RoomEvent.ParticipantDisconnected, notifyRoster);
            room.on(RoomEvent.Disconnected, (reason) => notifyStatus('disconnected', reason ? String(reason) : 'ارتباط با اتاق قطع شد.'));
            notifyStatus('connecting', 'در حال اتصال به اتاق...');
            await room.connect(url, token);
            notifyStatus('connected', 'به اتاق متصل شد.');
            return 'connected';
        },
        setStatusHandler(handler) {
            statusHandler = handler || null;
            if (!deviceChangeHandler && navigator.mediaDevices?.addEventListener) {
                deviceChangeHandler = () => notifyDevicesChanged();
                navigator.mediaDevices.addEventListener('devicechange', deviceChangeHandler);
            }
        },

        getConnectionState() { return room?.state || 'disconnected'; },

        setChatHandler(handler) { chatHandler = handler || null; if (chatHandler) notifyRoster(); },
        getChatRoster() { return { me: room?.localParticipant?.name || room?.localParticipant?.identity || '', people: roster() }; },
        async sendChat(text, toIdentity) {
            const current = requireRoom();
            const clean = String(text || '').trim().slice(0, 1000);
            if (!clean) return null;
            const me = current.localParticipant;
            const msg = { id: (crypto.randomUUID ? crypto.randomUUID() : String(Date.now()) + Math.random()), scope: toIdentity ? 'private' : 'public', text: clean, name: me.name || me.identity, ts: Date.now() };
            const options = { reliable: true, topic: 'ls-chat' };
            if (toIdentity) options.destinationIdentities = [toIdentity];
            await me.publishData(new TextEncoder().encode(JSON.stringify(msg)), options);
            return JSON.stringify({ ...msg, from: me.identity, mine: true, to: toIdentity || null });
        },

        async connect(url, token, previewId) {
            if (!window.LivekitClient) throw new Error('SDK رسمی LiveKit بارگذاری نشده است. اتصال اینترنت یا CDN را بررسی کنید.');
            if (room) await this.disconnect();

            const { Room, RoomEvent, Track, VideoPresets } = window.LivekitClient;
            const audioProfile = window.audioMixer?.load?.() || {};
            room = new Room({
                adaptiveStream: true,
                dynacast: true,
                videoCaptureDefaults: { resolution: VideoPresets.h720.resolution },
                audioCaptureDefaults: {
                    echoCancellation: audioProfile.echoCancellation !== false,
                    noiseSuppression: audioProfile.noiseSuppression !== false,
                    autoGainControl: !!audioProfile.autoGainControl
                }
            });

            room.on(RoomEvent.TrackSubscribed, (track) => {
                if (track.kind === Track.Kind.Video) {
                    const remote = document.getElementById('remotePreview');
                    if (remote) track.attach(remote);
                }
            });
            room.on(RoomEvent.TrackUnsubscribed, (track) => track.detach());
            room.on(RoomEvent.DataReceived, (payload, participant, kind, topic) => {
                if (topic === 'ls-ctl' && !isInfra(participant)) { try { window.lsTeam?.onControl(JSON.parse(new TextDecoder().decode(payload)), participant); } catch (_) { } return; }
                if (topic !== 'ls-chat' || isInfra(participant)) return;
                try {
                    const msg = JSON.parse(new TextDecoder().decode(payload));
                    msg.from = participant?.identity || ''; msg.name = msg.name || participant?.name || participant?.identity || '';
                    chatHandler?.invokeMethodAsync('OnChatMessage', JSON.stringify(msg));
                } catch (_) { }
            });
            room.on(RoomEvent.ParticipantConnected, notifyRoster);
            room.on(RoomEvent.ParticipantDisconnected, notifyRoster);
            room.on(RoomEvent.Disconnected, (reason) => {
                showPreview(false);
                cameraTrack = null;
                screenTrack = null;
                programActive = false;
                releaseProgramAssets();
                clearLiveProcessedAudio().catch(() => {});
                liveRawAudioTrack = null;
                notifyStatus('disconnected', reason ? String(reason) : 'ارتباط با اتاق قطع شد.');
            });
            if (RoomEvent.Reconnecting) room.on(RoomEvent.Reconnecting, () => notifyStatus('reconnecting', 'ارتباط قطع شده؛ LiveKit در حال تلاش برای اتصال مجدد است.'));
            if (RoomEvent.Reconnected) room.on(RoomEvent.Reconnected, () => {
                notifyStatus('connected', 'ارتباط LiveKit بازیابی شد.');
                cameraTrack = room?.localParticipant.getTrackPublication(Track.Source.Camera)?.track ?? null;
                screenTrack = room?.localParticipant.getTrackPublication(Track.Source.ScreenShare)?.track ?? null;
                if (cameraTrack && !screenTrack) attachVideo(cameraTrack, cameraElement(previewId));
            });
            if (RoomEvent.ConnectionStateChanged) room.on(RoomEvent.ConnectionStateChanged, state => {
                if (state === 'connecting') notifyStatus('connecting', 'در حال برقراری ارتباط با LiveKit...');
                else if (state === 'connected') notifyStatus('connected', 'به اتاق LiveKit متصل است.');
                else if (state === 'reconnecting') notifyStatus('reconnecting', 'در حال بازیابی ارتباط...');
                else if (state === 'disconnected') notifyStatus('disconnected', 'ارتباط با اتاق قطع شد.');
            });
            if (RoomEvent.MediaDevicesError) room.on(RoomEvent.MediaDevicesError, error => notifyStatus('device-error', error?.message || 'خطا در دسترسی به دستگاه رسانه.'));
            room.on(RoomEvent.LocalTrackUnpublished, (publication) => {
                if (publication.source === Track.Source.Camera) {
                    cameraTrack = null;
                    showPreview(false);
                }
                if (publication.source === Track.Source.ScreenShare) screenTrack = null;
            });

            try {
                notifyStatus('connecting', 'در حال اتصال به LiveKit...');
                await room.connect(url, token);
                await room.localParticipant.setCameraEnabled(true);
                await room.localParticipant.setMicrophoneEnabled(true);
                liveRawAudioTrack = room.localParticipant.getTrackPublication(Track.Source.Microphone)?.track?.mediaStreamTrack ?? null;
                try { await applyLiveProcessedAudio(); } catch (audioError) { console.warn('LiveStudio audio processing fallback:', audioError); }
                try { await publishSfxTrack(); } catch (sfxError) { console.warn('LiveStudio soundboard track unavailable:', sfxError); }
                try { window.lsTeam?.hostReady?.(); } catch (_) { }
                cameraTrack = room.localParticipant.getTrackPublication(Track.Source.Camera)?.track ?? null;
                attachVideo(cameraTrack, cameraElement(previewId));
                notifyStatus('connected', 'اتصال برقرار است.');
                return 'connected';
            } catch (error) {
                notifyStatus('error', error?.message || 'اتصال به LiveKit ناموفق بود.');
                await this.disconnect();
                throw new Error(error?.message || 'اتصال به LiveKit ناموفق بود.');
            }
        },

        async startProgramOutput() { return await createProgramOutput(); },
        async stopProgramOutput() { return await stopProgramOutput(); },
        isProgramOutputActive() { return programActive; },

        async setCamera(enabled, deviceId) {
            const current = requireRoom();
            const options = deviceId ? { deviceId } : undefined;
            await current.localParticipant.setCameraEnabled(enabled, options);
            const track = current.localParticipant.getTrackPublication(window.LivekitClient.Track.Source.Camera)?.track ?? null;
            cameraTrack = track;
            if (enabled) attachVideo(track, cameraElement());
            else showPreview(false);
            return enabled;
        },

        async setMicrophone(enabled, deviceId) {
            const current = requireRoom();
            if (!enabled) {
                await clearLiveProcessedAudio(); liveRawAudioTrack = null;
                await current.localParticipant.setMicrophoneEnabled(false);
                return false;
            }
            await clearLiveProcessedAudio();
            const options = deviceId ? { deviceId } : undefined;
            await current.localParticipant.setMicrophoneEnabled(true, options);
            liveRawAudioTrack = current.localParticipant.getTrackPublication(window.LivekitClient.Track.Source.Microphone)?.track?.mediaStreamTrack ?? null;
            await applyLiveProcessedAudio();
            return true;
        },

        async refreshMicrophoneProcessing() {
            if (!room || room.state !== 'connected') return false;
            if (!liveRawAudioTrack || liveRawAudioTrack.readyState !== 'live') {
                const pub = room.localParticipant.getTrackPublication(window.LivekitClient.Track.Source.Microphone);
                liveRawAudioTrack = pub?.track?.mediaStreamTrack ?? null;
            }
            return await applyLiveProcessedAudio();
        },

        async setScreenShare(enabled) {
            const current = requireRoom();
            await current.localParticipant.setScreenShareEnabled(enabled, { audio: false });
            const source = window.LivekitClient.Track.Source.ScreenShare;
            screenTrack = current.localParticipant.getTrackPublication(source)?.track ?? null;
            if (enabled && screenTrack) attachVideo(screenTrack, cameraElement());
            else if (cameraTrack) attachVideo(cameraTrack, cameraElement());
            else showPreview(false);
            return enabled;
        },

        async selectCamera(deviceId) {
            const current = requireRoom();
            await current.localParticipant.setCameraEnabled(false);
            await current.localParticipant.setCameraEnabled(true, { deviceId });
            cameraTrack = current.localParticipant.getTrackPublication(window.LivekitClient.Track.Source.Camera)?.track ?? null;
            if (!screenTrack) attachVideo(cameraTrack, cameraElement());
        },

        async selectMicrophone(deviceId) {
            const current = requireRoom();
            await clearLiveProcessedAudio(); liveRawAudioTrack = null;
            await current.localParticipant.setMicrophoneEnabled(false);
            await current.localParticipant.setMicrophoneEnabled(true, { deviceId });
            liveRawAudioTrack = current.localParticipant.getTrackPublication(window.LivekitClient.Track.Source.Microphone)?.track?.mediaStreamTrack ?? null;
            await applyLiveProcessedAudio();
        },

        async getDevices() {
            const devices = await navigator.mediaDevices.enumerateDevices();
            return {
                cameras: devices.filter(x => x.kind === 'videoinput').map(x => ({ deviceId: x.deviceId, label: x.label || 'دوربین' })),
                microphones: devices.filter(x => x.kind === 'audioinput').map(x => ({ deviceId: x.deviceId, label: x.label || 'میکروفن' }))
            };
        },

        async disconnect() {
            await clearLiveProcessedAudio(); liveRawAudioTrack = null;
            if (room && programActive) { try { await stopProgramOutput(); } catch (_) { releaseProgramAssets(); } }
            if (room) {
                try { await room.disconnect(); } finally { room = null; }
            }
            cameraTrack = null;
            screenTrack = null;
            const video = cameraElement();
            if (video) {
                try { video.pause(); video.srcObject = null; } catch { }
                video.removeAttribute('src');
            }
            showPreview(false);
            notifyStatus('disconnected', 'اتصال قطع شد.');
        }
    };

    if (navigator.mediaDevices?.addEventListener) {
        deviceChangeHandler = () => notifyDevicesChanged();
        navigator.mediaDevices.addEventListener('devicechange', deviceChangeHandler);
    }
})();
