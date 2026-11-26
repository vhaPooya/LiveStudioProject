# راهنمای انتشار LiveStudio

1. سرور با Docker + Compose؛ دو دامنه (سایت و LiveKit) روی IP سرور (رکورد A) و پورت‌های 80/443 باز.
2. `cp .env.production.example .env` و مقادیر را پر کنید (`Platform__InternalApiKey` تصادفی ≥۲۴ کاراکتر، `Admin__Password` قوی).
3. استک موجود LiveKit/Egress را بدون تغییر نسخه اجرا کنید؛ کلید/سکرت آن باید با `LiveKit__*` در `.env` یکی باشد.
4. اجرا: `docker compose -f docker-compose.prod.yml up -d --build`
5. بررسی سلامت: `https://<SITE_DOMAIN>/healthz`
6. ورود به `/admin`:
   - کلید OTPy را در تنظیمات وارد و ارسال پیامک را روشن کنید (`Otp:DevBypassCode` نباید وجود داشته باشد).
   - درگاه پرداخت را تنظیم کنید (شبیه‌ساز در Production غیرفعال است).
   - در Google Cloud، Redirect URI را ثبت کنید: `https://<SITE_DOMAIN>/oauth/google/callback`
7. پشتیبان: volume با نام `app-data` (SQLite و کلیدهای Data Protection) را مرتب بکاپ بگیرید؛ بدون `keys` کوکی‌ها و توکن‌های رمزشده قابل خواندن نیستند.
8. اگر تنظیمات ناامن باشد، Web در Production عمداً بالا نمی‌آید و علت را در لاگ می‌نویسد.

## ذخیره‌سازی Media Box و Google Drive
- تصاویر/SVG/صدای سبک در `/data/media` (همان volume `app-data`) ذخیره می‌شوند؛ بکاپ این volume شامل آن‌ها هم هست. متغیر `Media__RootPath` در compose تنظیم شده است.
- ویدئوها فقط در Google Drive کاربر (پوشه‌ی «LiveStudio Media») ذخیره می‌شوند؛ در Google Cloud پروژه‌ی کاربر، «Google Drive API» باید فعال باشد. اسکوپ `drive.file` است.
- حجم آپلود ویدئو (تا ۱GB) از مدار Blazor عبور می‌کند؛ `Caddyfile` و پراکسی جلو را روی timeout مناسب نگه دارید.

## چک‌لیست انتشار
1. `.env` را از `.env.production.example` بسازید؛ همه‌ی مقادیر `replace-with-...` را عوض کنید (`Platform__InternalApiKey`، `Admin__Password`، `LiveKit__ApiSecret`).
2. `ASPNETCORE_ENVIRONMENT=Production` و `Platform__AllowPaymentSimulator=false` (برنامه در غیر این صورت هنگام استارت خطا می‌دهد).
3. دامنه‌ها (`SITE_DOMAIN`، `LIVEKIT_DOMAIN`) روی DNS و پورت‌های ۸۰/۴۴۳ و UDP/TCP مربوط به LiveKit باز باشد.
4. `docker compose -f docker-compose.prod.yml up -d --build`
5. تست دودی: ورود/ثبت‌نام → اتصال OAuth یوتیوب → اتصال Drive → آپلود تصویر و ویدئو در Media Box → لایو آزمایشی خصوصی با دو مقصد.
6. بکاپ زمان‌بندی‌شده از volume `app-data`.
