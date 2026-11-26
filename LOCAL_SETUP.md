# LiveStudio – اجرای مرحله اتصال LiveKit

## تنظیمات محلی

مقادیر حساس را در PowerShell و در همان ترمینالی که API را اجرا می‌کنید تنظیم کنید؛ آن‌ها را داخل Git یا ZIP قرار ندهید:

```powershell
$env:GoogleOAuth__ClientId = "YOUR_GOOGLE_CLIENT_ID"
$env:GoogleOAuth__ClientSecret = "YOUR_GOOGLE_CLIENT_SECRET"
$env:LiveKit__ServerUrl = "http://localhost:7880"
$env:LiveKit__PublicUrl = "ws://localhost:7880"
$env:LiveKit__ApiKey = "devkey"
$env:LiveKit__ApiSecret = "SAME_SECRET_AS_LIVEKIT_YML"
```

`LiveKit__ApiSecret` باید دقیقاً با Secret بخش `keys` در فایل `livekit.yml` یکسان باشد. برای محیط عمومی، `PublicUrl` باید آدرس HTTPS/WSS معتبر سرور LiveKit باشد.

## اجرا

ترمینال اول – API:

```powershell
cd F:\LiveStudio
 dotnet restore .\LiveStudio.sln
 dotnet run --project .\src\LiveStudio.Api\LiveStudio.Api.csproj --urls http://localhost:5080
```

ترمینال دوم – رابط وب:

```powershell
cd F:\LiveStudio
 dotnet run --project .\src\LiveStudio.Web\LiveStudio.Web.csproj --urls http://localhost:5200
```

سرویس‌های Redis و LiveKit که از قبل اجرا هستند باید روشن بمانند. نیازی به Pull مجدد Imageها نیست.

## تست دستی

1. داشبورد را در `http://localhost:5200` باز کنید و اتصال YouTube را بررسی کنید.
2. یک Broadcast بسازید یا از فهرست، «ورود به استودیو» را انتخاب کنید.
3. در صفحه استودیو، اتصال را بزنید و مجوز دوربین و میکروفن را بدهید.
4. تصویر باید در پیش‌نمایش دیده شود؛ انتخاب دستگاه و اشتراک صفحه نیز در همان صفحه در دسترس است.
5. وضعیت API LiveKit از `http://localhost:5080/api/v1/livekit/status` قابل بررسی است.

## داده‌های محلی قبلی

برای جلوگیری از انتشار اطلاعات محرمانه، فایل دیتابیس SQLite و کلیدهای Data Protection در این بسته قرار نگرفته‌اند. هنگام جایگزینی نسخه قبلی، فایل `src\LiveStudio.Api\livestudio.db` و پوشه `src\LiveStudio.Api\.keys` را از نسخه فعلی خود نگه دارید و به پوشه جدید منتقل کنید. مقادیر OAuth نیز باید از Secret Manager یا متغیرهای محیطی بارگذاری شوند.

## محدودیت این مرحله

این مرحله اتصال مرورگر، انتشار دوربین/میکروفن و اشتراک صفحه در اتاق LiveKit را پیاده‌سازی می‌کند. Egress و شروع RTMP به YouTube هنوز در این مرحله فعال نشده‌اند. احراز هویت کامل کاربران و جداسازی Tenantها نیز هنوز در پروژه پیاده‌سازی نشده‌اند؛ این نسخه را روی اینترنت عمومی بدون لایه احراز هویت قرار ندهید.


## Stage 9 — compositor smoke test

After starting the Web project, open Live Studio from the sidebar and connect the camera. In the integrated compositor, choose a background from Media Box, add a logo/image or text layer, select the Element tab and drag the element directly on the Preview canvas. Adjust its size, position, brightness, contrast, saturation, exposure, hue and animation. The smaller Program monitor must remain unchanged until TAKE is pressed. Press TAKE and verify the Program monitor changes; when LiveKit Program Output is enabled, this Program canvas is the published camera track.

In Creator Studio, activate the camera and add the same types of layers. The large compositor monitor is the recording canvas; start/stop recording and verify the downloaded WebM contains the same composed picture and processed microphone audio.

Use localhost for camera access. Scene settings are stored separately per studio in browser localStorage, while uploaded media stays in the Media Box IndexedDB database. The current stage has not been built or exercised with a real camera/LiveKit session in the build environment; perform this smoke test before relying on it for a live broadcast.

### Stage 10 — Scene Library

Use the **صحنه‌ها** tab in either studio to save and load named scenes, export/import scene JSON, or save a PNG snapshot of Preview. Named scenes are stored locally in the browser (up to 40 per studio). JSON files do not embed Media Box source files; add the referenced assets to Media Box on another device before importing if you need the layers to render there.
