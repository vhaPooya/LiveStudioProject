# LiveStudio

پروژه آغازین پلتفرم مدیریت حرفه‌ای YouTube Live با .NET 10 و Blazor.

## پیش‌نیازها
- .NET 10 SDK
- دسترسی اینترنت برای دریافت NuGet packages

## اجرا

1. API را اجرا کنید:

```bash
dotnet run --project src/LiveStudio.Api
```

API: `http://localhost:5080`  
Health: `http://localhost:5080/health`  
OpenAPI (Development): `http://localhost:5080/openapi/v1.json`

2. داشبورد را در ترمینال دیگر اجرا کنید:

```bash
dotnet run --project src/LiveStudio.Web --urls http://localhost:5200
```

Dashboard: `http://localhost:5200`

## وب‌سایت، پنل کاربری و پنل مدیریت (فاز ۶)

- صفحه معرفی: `/` — طرح‌ها، سوالات متداول و متن‌ها از پنل مدیریت قابل ویرایش‌اند.
- ورود/ثبت‌نام با کد ملی + موبایل و پیامک OTPy: `/login` و `/register`. کلید API در «مدیریت › تنظیمات سامانه › پنل پیامک» (یا `Otpy__ApiKey`) ثبت می‌شود.
- پنل مشتری: `/panel` (داشبورد)، `/panel/settings` (مشخصات، اشتراک، **Google OAuth Settings**)، `/panel/subscription`، `/panel/tickets`.
- پنل مدیریت: `/admin/login` — مدیر اصلی با `Admin:Username` / `Admin:Password` (در Development: `admin` / `LiveStudio@Admin2026`). کاربران مدیریتی و دسترسی‌ها از «کاربران مدیریت».
- در محیط Development کد OTP ثابت `123456` پذیرفته می‌شود (`Otp:DevBypassCode`، فقط وقتی `ASPNETCORE_ENVIRONMENT=Development`). درگاه «شبیه‌ساز» نیز فقط با `Platform:AllowPaymentSimulator=true` فعال است.

### Google OAuth اختصاصی هر مشتری
هر مشتری Client ID، Client Secret و Project ID پروژه Google Cloud خودش را در پنل ثبت می‌کند (ویدئوی آموزشی از «مدیریت › تنظیمات سامانه» تعیین می‌شود). Redirect URI مشترک نرم‌افزار `{Platform:PublicBaseUrl}/oauth/google/callback` است (قابل تغییر در پنل مدیریت و باید HTTPS باشد). Secret با ASP.NET Data Protection و کلید جداگانه برای هر مشتری رمزنگاری می‌شود، هرگز دوباره نمایش داده نمی‌شود و توکن‌ها فقط برای همان مشتری استفاده می‌شوند.

مسیر: اعتبارسنجی → ذخیره رمزنگاری‌شده → ساخت درخواست (PKCE + state یک‌بارمصرف) → Consent Screen گوگل → Callback → تبادل Code و تست دسترسی به کانال → وضعیت `Connected`.

### نکات استقرار
- پروژه‌های Web و Api باید `Platform:InternalApiKey` یکسان داشته باشند و فایل SQLite و کلیدهای Data Protection را در یک پوشه مشترک (`Platform:DataDirectory`، پیش‌فرض `<solution>/data`) ببینند.
- API فقط با کلید داخلی و هدر `X-Tenant-Id` از طرف Web قابل فراخوانی است و اشتراک فعال را برای عملیات پخش اجباری می‌کند.

Scopeها شامل `youtube.readonly` برای خواندن کانال و `youtube` برای عملیات مدیریت Broadcast هستند. ساخت Broadcast از API واقعی `liveBroadcasts.insert` استفاده می‌کند (هزینه سهمیه معمول این درخواست ۵۰ واحد است)؛ فهرست‌گیری از `liveBroadcasts.list` استفاده می‌کند. ساخت Broadcast به‌صورت پیش‌فرض Private است. سهمیه روزانه پروژه در Google Cloud Console قابل مشاهده است.

**محدودیت‌های نسخه فعلی:** این Starter تک‌اتصالی است و هنوز احراز هویت کاربران LiveStudio، جداسازی tenant، مدیریت چند کانال، عملیات bind/transition و migrationهای نسخه‌بندی‌شده را ندارد. برای محیط Production باید این موارد، HTTPS، ذخیره پایدار کلیدهای Data Protection و سیاست پشتیبان‌گیری تکمیل شوند. در نسخه فعلی ساخت اولیه دیتابیس با `EnsureCreated` انجام می‌شود؛ برای Production باید به Migrationهای نسخه‌بندی‌شده تبدیل شود.

## وضعیت فعلی
- Solution چندپروژه‌ای
- مدل‌های پایه کانال و Broadcast
- DbContext و پیکربندی SQLite
- OAuth واقعی Google با PKCE و callback، رمزنگاری tokenها در دیتابیس و تمدید access token
- دریافت کانال متصل و فهرست/ایجاد واقعی YouTube Live Broadcast
- API وضعیت سرویس و قراردادهای YouTube
- داشبورد Blazor فارسی، واکنش‌گرا و مبتنی بر Bootstrap 5 RTL با اتصال به API

## توجه
این نسخه Starter است و هنوز محصول کامل یا آماده Production نیست. پیش از استقرار عمومی باید احراز هویت، OAuth واقعی، مجوزها، رمزنگاری Tokenها، Migrationها و تست‌های امنیتی تکمیل شوند.

## YouTube channel API

After connecting a YouTube account, retrieve the connected channel profile and public statistics with:

`GET /api/v1/youtube/channel`

The response includes channel ID, title, description, custom URL, thumbnail, creation date, country, subscriber count (when visible), view count, and video count. Broadcast listing is available at `GET /api/v1/youtube/broadcasts`.

### Local OAuth configuration

Do not commit OAuth credentials. Configure these environment variables for the API process:

- `GoogleOAuth__ClientId`
- `GoogleOAuth__ClientSecret`
- `GoogleOAuth__RedirectUri`

The redirect URI must exactly match the authorized redirect URI in Google Cloud Console. The SQLite database and ASP.NET Data Protection keys are local runtime data and are intentionally excluded from source archives.

## Live encoder workflow

LiveStudio manages the YouTube Live lifecycle. For reliable camera, microphone, scene, screen-share and encoder handling, use OBS Studio as the encoder (the browser itself cannot publish RTMP to YouTube). When a broadcast is created, LiveStudio now creates a matching YouTube Live Stream and binds it to the broadcast. The dashboard shows the RTMP server and stream key returned by YouTube.

1. Create a broadcast in LiveStudio.
2. In OBS Studio open **Settings → Stream**.
3. Choose **YouTube - RTMPS** (or Custom if needed), and enter the server and stream key shown by LiveStudio.
4. Add a Video Capture Device and Audio Input Capture source in OBS; configure scenes and preview them there.
5. Click **Start Streaming** in OBS and wait for YouTube to detect the incoming encoder.
6. Use **Start broadcast** in LiveStudio to transition the YouTube event to live. Use **End broadcast** to complete it.

Keep the stream key private. The key is returned to the dashboard only in the create response; create a new broadcast/stream if it is lost. OBS must be running and sending a healthy signal before transitioning a scheduled broadcast to live.

### API lifecycle endpoints

- `POST /api/v1/youtube/broadcasts` creates a broadcast, creates its RTMP ingestion stream, and binds the two.
- `POST /api/v1/youtube/broadcasts/{id}/transition/live` starts the YouTube broadcast after the encoder is online.
- `POST /api/v1/youtube/broadcasts/{id}/transition/complete` ends the broadcast.
- `GET /api/v1/youtube/broadcasts/{id}` retrieves a broadcast.

## رابط کاربری Bootstrap

رابط قبلی مبتنی بر Fluent UI حذف شده و رابط جدید با Bootstrap 5 RTL و Bootstrap Icons بازنویسی شده است. فایل CSS اختصاصی برنامه پس از Bootstrap بارگذاری می‌شود. برای تغییر آدرس API داشبورد، مقدار `Api:BaseUrl` را در تنظیمات Web تغییر دهید.

پس از OAuth، API کاربر را به داشبورد بازمی‌گرداند. آدرس داشبورد برای callback از `Dashboard:BaseUrl` در تنظیمات API خوانده می‌شود (پیش‌فرض توسعه: `http://localhost:5200`). این آدرس باید با محل واقعی اجرای داشبورد هماهنگ باشد.

## اصلاح مسیر ورود به استودیو
- از منوی کناری روی «ورود به استودیو» کلیک کنید.
- مسیر `/studio` فهرست پخش‌های فعال/قابل استفاده را نشان می‌دهد.
- با انتخاب هر پخش، به `/studio/{BroadcastId}` وارد می‌شوید.
- در نسخه موبایل نیز میانبر «استودیو» کنار نام LiveStudio قرار دارد.

## LiveKit local development
در `src/LiveStudio.Api/appsettings.Development.json` مقدار LiveKit:ApiSecret با Secret محیط آزمایشی ارائه‌شده تنظیم شده است.
این مقدار فقط برای توسعه محلی است و نباید در محیط Production استفاده شود.


## Integrated Creator Studio

- داشبورد شامل دو ورودی مستقل برای Live Studio و Creator Studio است.
- مسیر `/creator` امکان دسترسی به دوربین و میکروفون، انتخاب دستگاه‌ها، پیش‌نمایش، ضبط محلی با MediaRecorder و دانلود فایل WebM را فراهم می‌کند.
- پنل تله‌پرومپتر اولیه برای نگهداری و نمایش متن اجرا اضافه شده است.
- ضبط در مرورگر و روی دستگاه کاربر انجام می‌شود و در این مرحله فایل به سرور ارسال نمی‌شود.
- برای ضبط، دسترسی دوربین/میکروفون مرورگر لازم است. در مرورگرهای مدرن localhost به‌عنوان بستر توسعه امن پشتیبانی می‌شود.
- امکانات تدوین چندترکه، ضبط چنددوربینه هم‌زمان، پیمایش خودکار تله‌پرومپتر و گردش‌کار AI در مراحل بعدی توسعه قرار دارند.

## Creator tools

- `/creator`: browser-based local video/audio recording workspace.
- `/teleprompter`: dedicated smart teleprompter with adjustable scroll speed, font size and line spacing, mirror mode, fullscreen, and keyboard shortcuts.

The dedicated legacy teleprompter route remains in the project, but its navigation shortcut is hidden while its workflow is consolidated into Creator Studio.

## Stage 3 - Media Box

The integrated Media Box is available from the navigation at `/media-box`. It includes local IndexedDB media storage, demo assets for all media categories, logo placement and animations, broadcast lower-thirds and breaking-news graphics. The saved scene design is shared by the Live Studio and Creator Studio preview layers. See `MEDIA_BOX_STAGE3.md` for detailed implementation notes and current limitations.

## Live Program Output (Stage 5)

Live Studio now includes an optional **فعال‌سازی خروجی گرافیکی** control. It composites the local camera with the current Media Box background, logo animation, lower-third and breaking-news overlay on a 1080p/30fps canvas and publishes that canvas as the participant's LiveKit camera track. The microphone remains a separate published track. Use the stop control to restore the raw camera track. Screen share is still published separately and is not yet composited. This feature publishes to LiveKit; it does not replace the RTMP/YouTube encoder workflow. See `LIVE_PROGRAM_OUTPUT_STAGE5.md` for details and limitations.


## Stage 9 — Integrated Studio Compositor

The Live Studio and Creator Studio now use the same interactive compositor workspace. It renders the actual camera input, selected Media Box background, image/logo/video overlays, text, lower thirds and breaking-news graphics on a 1080p/30fps canvas. Elements can be dragged on the preview, ordered, hidden, locked and individually color-corrected (brightness, contrast, saturation, exposure and hue). Logo and text animation presets include fade, slides, zoom, pulse, float, bounce, rotate and glow. Camera correction and chroma key are applied to the real camera source before compositing.

Live Studio has separate Preview and Program monitors: changes are prepared in Preview and sent to Program with TAKE. LiveKit publishes the Program canvas track. Creator Studio records the same compositor canvas shown in its monitor, with the processed microphone track. Relevant controls are embedded in each studio; their standalone navigation links have been removed. See `COMPOSITOR_STAGE9.md` for the workflow and limitations.

**Verification:** all JavaScript files passed `node --check`. The .NET SDK was not available in the build environment, so a .NET build and real camera/LiveKit test remain to be run locally.

## Stage 10 — Scene Library

Both Live Studio and Creator Studio now include a Scene Library for named scene saves, loading, portable JSON import/export, and 1080p Preview PNG snapshots. Scene JSON stores layer metadata and settings, not the original Media Box files; destination devices need matching Media Box assets. See `SCENE_LIBRARY_STAGE10.md`.


Finalization Phase 1: a browser diagnostics page is available at /diagnostics. See FINALIZATION_PHASE1.md for implemented checks, validation scope, and known production-hardening items.


## Finalization Phase 2 — LiveKit resilience

LiveKit connection lifecycle feedback, reconnect state handling, media-device change refresh, and unexpected-disconnect resource cleanup have been added. See `FINALIZATION_PHASE2_LIVEKIT.md` for implementation details and verification limits.

## Finalization Phase 3 — Creator recording

Creator Studio now supports pause/resume during recording, an elapsed-time indicator that excludes paused periods, selected-quality bitrate targets, and a download extension based on the browser's actual recording MIME type. See `FINALIZATION_PHASE3_CREATOR_RECORDING.md` for scope and compatibility notes. Browser MediaRecorder output is not guaranteed to be MP4; MP4 transcoding is a separate future phase.


## Phase 4 — Direct YouTube RTMP via existing LiveKit Egress

See `FINALIZATION_PHASE4_YOUTUBE_EGRESS.md`. This phase controls the already-running Egress worker from the .NET backend. It does not create another Egress container and keeps SQLite as the application database. YouTube RTMP credentials remain server-side.
