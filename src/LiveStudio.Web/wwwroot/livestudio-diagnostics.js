(() => {
    "use strict";

    const result = (name, status, details) => ({ name, status, details });

    async function checkIndexedDb() {
        if (!("indexedDB" in window)) return result("ذخیره‌سازی Media Box", "error", "مرورگر IndexedDB را پشتیبانی نمی‌کند.");
        const dbName = `ls-diagnostic-${Date.now()}`;
        return await new Promise(resolve => {
            let settled = false;
            const finish = value => {
                if (settled) return;
                settled = true;
                try { indexedDB.deleteDatabase(dbName); } catch (_) { }
                resolve(value);
            };
            try {
                const request = indexedDB.open(dbName, 1);
                request.onupgradeneeded = () => request.result.createObjectStore("probe");
                request.onsuccess = () => {
                    request.result.close();
                    finish(result("ذخیره‌سازی Media Box", "ok", "IndexedDB قابل دسترسی است."));
                };
                request.onerror = () => finish(result("ذخیره‌سازی Media Box", "error", "مرورگر اجازه استفاده از IndexedDB را نداد."));
                request.onblocked = () => finish(result("ذخیره‌سازی Media Box", "warning", "درخواست بررسی IndexedDB مسدود شد."));
                setTimeout(() => finish(result("ذخیره‌سازی Media Box", "warning", "بررسی IndexedDB بیش از حد طول کشید.")), 4000);
            } catch (error) {
                finish(result("ذخیره‌سازی Media Box", "error", error?.message || "خطای ناشناخته."));
            }
        });
    }

    function checkLocalStorage() {
        try {
            const key = "__livestudio_diagnostic__";
            localStorage.setItem(key, "ok");
            const valid = localStorage.getItem(key) === "ok";
            localStorage.removeItem(key);
            return result("ذخیره تنظیمات و صحنه‌ها", valid ? "ok" : "error",
                valid ? "Local Storage قابل نوشتن و خواندن است." : "خواندن مقدار آزمایشی ناموفق بود.");
        } catch (_) {
            return result("ذخیره تنظیمات و صحنه‌ها", "error", "ذخیره‌سازی مرورگر غیرفعال یا محدود شده است.");
        }
    }

    function checkCanvasCapture() {
        try {
            const canvas = document.createElement("canvas");
            canvas.width = canvas.height = 2;
            const supported = typeof canvas.captureStream === "function";
            if (!supported) return result("خروجی تصویر Compositor", "error", "Canvas captureStream در این مرورگر پشتیبانی نمی‌شود.");
            const stream = canvas.captureStream(1);
            const track = stream.getVideoTracks()[0];
            const valid = !!track;
            stream.getTracks().forEach(t => t.stop());
            return result("خروجی تصویر Compositor", valid ? "ok" : "error",
                valid ? "ساخت VideoStream از Canvas امکان‌پذیر است." : "ساخت ترک ویدئو از Canvas ناموفق بود.");
        } catch (error) {
            return result("خروجی تصویر Compositor", "error", error?.message || "ساخت خروجی Canvas ناموفق بود.");
        }
    }

    function checkMediaRecorder() {
        if (!("MediaRecorder" in window))
            return result("ضبط ویدئو", "error", "MediaRecorder در این مرورگر وجود ندارد.");
        const types = ["video/webm;codecs=vp9,opus", "video/webm;codecs=vp8,opus", "video/webm"];
        const supported = types.filter(type => MediaRecorder.isTypeSupported?.(type));
        return supported.length
            ? result("ضبط ویدئو", "ok", `فرمت‌های قابل استفاده: ${supported.join("، ")}`)
            : result("ضبط ویدئو", "warning", "MediaRecorder موجود است اما فرمت WebM شناخته‌شده‌ای تأیید نشد.");
    }

    function checkWebRtc() {
        const supported = !!(window.RTCPeerConnection && navigator.mediaDevices?.getUserMedia);
        return result("ارتباط WebRTC و دوربین", supported ? "ok" : "error",
            supported ? "WebRTC و API دریافت رسانه در دسترس هستند." : "مرورگر WebRTC یا getUserMedia را پشتیبانی نمی‌کند.");
    }

    function checkAudio() {
        const supported = !!(window.AudioContext || window.webkitAudioContext);
        return result("پردازش صدای Web Audio", supported ? "ok" : "error",
            supported ? "AudioContext برای میکسر و پردازش صدا موجود است." : "Web Audio API در دسترس نیست.");
    }

    function checkLiveKitSdk() {
        const sdk = window.LivekitClient;
        const supported = !!(sdk?.Room && sdk?.RoomEvent && sdk?.Track);
        return result("کتابخانه LiveKit", supported ? "ok" : "error",
            supported ? "SDK سمت مرورگر بارگذاری شده است." : "SDK LiveKit بارگذاری نشده؛ اتصال CDN یا دسترسی اینترنت را بررسی کنید.");
    }

    function checkSecureContext() {
        const secure = window.isSecureContext || location.hostname === "localhost" || location.hostname === "127.0.0.1";
        return result("بستر امن مرورگر", secure ? "ok" : "warning",
            secure ? "صفحه در بستر امن یا localhost اجرا شده است." : "برای دسترسی دوربین و میکروفون، HTTPS لازم است.");
    }

    function checkPermissions() {
        if (!navigator.permissions?.query)
            return result("وضعیت مجوزهای رسانه", "warning", "مرورگر امکان بررسی وضعیت مجوزها را ارائه نمی‌کند؛ این مورد به‌تنهایی به معنی رد شدن مجوز نیست.");
        return Promise.all(["camera", "microphone"].map(async name => {
            try {
                const state = await navigator.permissions.query({ name });
                return `${name === "camera" ? "دوربین" : "میکروفون"}: ${state.state}`;
            } catch (_) {
                return `${name === "camera" ? "دوربین" : "میکروفون"}: وضعیت نامشخص`;
            }
        })).then(states => {
            const denied = states.some(x => x.endsWith("denied"));
            const prompt = states.some(x => x.endsWith("prompt"));
            return result("وضعیت مجوزهای رسانه", denied ? "warning" : "ok",
                `${states.join(" | ")}. بررسی بدون درخواست مجوز انجام شد${prompt ? "؛ هنگام استفاده، اجازه دسترسی لازم است." : "."}`);
        });
    }

    function checkBrowser() {
        const ua = navigator.userAgent || "نامشخص";
        const browser = /Edg\//.test(ua) ? "Microsoft Edge" :
            /Chrome\//.test(ua) ? "Google Chrome" :
            /Firefox\//.test(ua) ? "Mozilla Firefox" :
            /Safari\//.test(ua) && !/Chrome\//.test(ua) ? "Safari" : "مرورگر ناشناخته";
        return result("مرورگر", "ok", `${browser} | ${navigator.platform || "سیستم‌عامل نامشخص"}`);
    }

    window.liveStudioDiagnostics = {
        async run() {
            const checks = [
                checkBrowser(),
                checkSecureContext(),
                checkWebRtc(),
                checkAudio(),
                checkMediaRecorder(),
                checkCanvasCapture(),
                checkLiveKitSdk(),
                checkLocalStorage(),
                checkPermissions()
            ];
            const items = await Promise.all(checks);
            items.push(await checkIndexedDb());
            return items;
        }
    };
})();
