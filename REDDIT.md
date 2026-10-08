# Reddit / social post (copy-paste)

## Title options

1. `I built a Windows Wi‑Fi captive portal gateway — accounts, speed limits, site filters, and VLESS in one app (v1.0)`
2. `AeroGate Pilot v1.0 — turn a Windows PC into a login Wi‑Fi hotspot with plans, filters, and built‑in Xray`
3. `[Release] AeroGate Pilot 1.0 — Ethernet in, Wi‑Fi out, captive portal + VPN core`

## Body (English)

Hey everyone — I'm **Hasanwlip** (Nodepilot). Today I'm open‑sourcing the first public release of something I've been building end‑to‑end:

**AeroGate Pilot** — a Windows app that turns a normal PC into a **Wi‑Fi gateway with a captive portal**.

Plug Ethernet in, share Wi‑Fi out. Phones join, hit a login page, then browse under **plans** you define (data, time, speed, devices). You can block or allow sites per plan (`*.domain` wildcards), and route users through a **built‑in Xray core** with `vless://` / `vmess://` / `trojan://` / `ss://` import + ping — or through Clash/v2rayN if you prefer.

### Why I built it
I wanted a single self‑contained tool for cafés, classrooms, offices, or home labs — not a stack of hotspot + proxy + router firmware hacks. One installer, one dashboard (English + Persian RTL), start/stop cleanly.

### What's in v1.0
- Windows Mobile Hotspot + ICS, with auto‑repair when DHCP won't start  
- Captive portal (custom logo, colors, CSS, guest mode)  
- Users & plans: data / time / session / validity / Kbps / max devices  
- Per‑plan website blocklist or allowlist  
- Built‑in **Xray** next to the app (VLESS & friends) + optional external SOCKS/HTTP port  
- Dashboard, diagnostics, activity log  
- Installer + portable win‑x64 build  

### Stack
.NET 8 · WPF · ASP.NET Core portal · SQLite · WinDivert · Xray-core  

### Links
- GitHub: https://github.com/Hasanwlip/AeroGatePilot  
- Releases: installer + portable zip under **Releases**

Feedback, stars, and issues welcome. If you break it in creative ways, tell me — that's how v1.1 gets better.

— Hasanwlip / Nodepilot

---

## متن فارسی (برای ردیت / تلگرام)

سلام — من **Hasanwlip (Nodepilot)** هستم. اولین نسخه عمومی پروژه‌ای که کامل خودم ساختم را منتشر کردم:

**AeroGate Pilot** — یک برنامه ویندوزی که کامپیوتر را تبدیل به **گیت‌وی وای‌فای با صفحه ورود** می‌کند.

اینترنت از کابل می‌آید، وای‌فای پخش می‌شود؛ گوشی وصل می‌شود، لاگین می‌کند، بعد با **پلن** شما (حجم، زمان، سرعت، تعداد دستگاه) آنلاین می‌ماند. می‌توانید سایت‌ها را مسدود/مجاز کنید (`*.دامنه`) و ترافیک را از **هسته Xray داخل برنامه** (لینک VLESS و مشابه + پینگ) رد کنید — یا از پورت Clash/v2rayN.

**نسخه ۱.۰:** هات‌اسپات + پورتال سفارشی، کاربران و پلن‌ها، فیلتر سایت، VPN داخلی، داشبورد انگلیسی/فارسی، نصب‌کننده و پرتابل.

گیت‌هاب: https://github.com/Hasanwlip/AeroGatePilot  

نظر و ستاره خوشحال‌کننده است.
