# فاز چهارم — انتشار مستقیم YouTube با LiveKit Egress

## تغییرات
- LiveStudio از Egress موجود استفاده می‌کند؛ کانتینر جدیدی ساخته نمی‌شود.
- درخواست‌های Egress از Backend دات‌نت و با `EgressServiceClient` ارسال می‌شوند.
- منبع ویدئو/صدا اتاق اختصاصی `broadcast-{YouTubeBroadcastId}` است.
- خروجی با قالب `single-speaker` و Encoding برابر H.264 1080p30 به RTMP ارسال می‌شود.
- نشانی RTMP و Stream Key فقط در Backend دریافت و به Egress فرستاده می‌شوند؛ هیچ‌کدام به مرورگر برگردانده نمی‌شوند.
- دیتابیس برنامه SQLite باقی می‌ماند. PostgreSQL از docker-compose حذف شده است.
- کانتینرهای LiveKit و Egress موجود کاربر دست‌نخورده باقی می‌مانند.

## جریان اجرا
1. در Live Studio به LiveKit وصل شوید.
2. خروجی Program را فعال کنید تا Canvas پردازش‌شده به‌عنوان Track در اتاق منتشر شود.
3. «شروع پخش به YouTube» را بزنید.
4. Backend شناسه Broadcast را بررسی می‌کند، Bound Stream را از YouTube API می‌خواند و RTMP URL را فقط در حافظه Backend می‌سازد.
5. Backend با LiveKit Egress API یک Room Composite Egress آغاز می‌کند.
6. وضعیت از Egress API خوانده می‌شود و در پنل استودیو نمایش داده می‌شود.
7. با توقف پخش یا خاموش‌کردن Program، Egress متوقف می‌شود.

## تنظیمات موردنیاز Backend
مقادیر زیر باید با LiveKit Server فعلی یکسان باشند:
- `LiveKit:ServerUrl` — برای اجرای API روی ویندوز میزبان: `http://localhost:7880`
- `LiveKit:PublicUrl` — برای مرورگر: `ws://localhost:7880`
- `LiveKit:ApiKey` — `devkey`
- `LiveKit:ApiSecret` — همان مقدار `keys.devkey` در تنظیمات LiveKit

در محیط Development، این مقادیر از `src/LiveStudio.Api/appsettings.Development.json` خوانده می‌شوند. در محیط عملیاتی از Environment Variables استفاده کنید.

## API
- `POST /api/v1/youtube/broadcasts/{id}/egress/start`
- `GET /api/v1/youtube/broadcasts/{id}/egress`
- `POST /api/v1/youtube/broadcasts/{id}/egress/stop`

پاسخ API فقط شناسه Egress، وضعیت و خطا را شامل می‌شود؛ RTMP URL و Stream Key هیچ‌وقت در پاسخ قرار نمی‌گیرند.

## Docker
از آنجا که `livestudio-egress`، `livestudio-livekit` و `livestudio-redis` از قبل توسط کاربر ساخته شده‌اند، این فاز هیچ دستور ساخت/حذف کانتینر ندارد. سرویس Egress باید به همان Redis و LiveKit Server موجود متصل بماند.

`docker-compose.yml` پروژه فقط Redis اختیاری برای نصب تازه را نگه می‌دارد؛ PostgreSQL حذف شده چون دیتابیس برنامه SQLite است. برای راه‌اندازی مجدد Redis موجود، Compose را اجرا نکنید.

## تست
Build کامل .NET در محیط تولید فایل انجام نشده، چون .NET SDK در محیط ساخت در دسترس نبود. ساختار ZIP و Syntax فایل‌های JavaScript بررسی می‌شوند؛ تست واقعی RTMP باید با Broadcast آزمایشی Private/Unlisted انجام شود.
